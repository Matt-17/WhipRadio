using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class NewsPackageProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    NewsFeedPollingService feedPolling,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    IStationMetrics metrics,
    ILogger<NewsPackageProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProductionBudget = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan BlockRetryDelay = TimeSpan.FromSeconds(3);
    private const int BlockMaxAttempts = 3;
    internal sealed record PackagePlan(DateTimeOffset TargetLocal, bool IncludeNews, bool IncludeWeather);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            const string kind = "news";
            var cycleStart = Stopwatch.GetTimestamp();
            try
            {
                await RunCycleAsync(stoppingToken);
                metrics.GenerationSucceeded(kind, Stopwatch.GetElapsedTime(cycleStart));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.GenerationFailed(kind);
                logger.LogError(ex, "News package production cycle failed");
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        StationSettings settings;
        PackagePlan? plan;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!settings.NewsEnabled && !settings.WeatherEnabled)
            {
                return;
            }

            if (await TryResumePendingPackageAsync(settings, ct))
            {
                return;
            }

            plan = ResolveNextPreparationPlan(settings, timeProvider.GetLocalNow());
            if (plan is null)
            {
                return;
            }

            var targetUtc = plan.TargetLocal.UtcDateTime;
            if (await db.NewsPackages.AsNoTracking()
                .AnyAsync(package => package.Kind == NewsPackageKind.TopOfHour && package.TargetUtc == targetUtc, ct))
            {
                return;
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        await ProducePackageAsync(settings, plan.TargetLocal.UtcDateTime, plan, ct, budget.Token);
    }

    private async Task<bool> TryResumePendingPackageAsync(StationSettings settings, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldestValidTarget = now.AddSeconds(
            -TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var expired = await db.NewsPackages
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying)
                    && package.TargetUtc < oldestValidTarget)
                .ToListAsync(ct);
            foreach (var package in expired)
            {
                package.Status = NewsPackageStatus.Failed;
                package.FailureReason = "Production did not finish before the top-of-hour late window.";
                package.ProductionState = null;
            }

            if (expired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            var pending = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying)
                    && package.TargetUtc >= oldestValidTarget)
                .OrderBy(package => package.TargetUtc)
                .FirstOrDefaultAsync(ct);
            if (pending is null)
            {
                return false;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ProductionBudget);
            var plan = BuildPackagePlan(settings, ToLocalTime(pending.TargetUtc, timeProvider.GetLocalNow().Offset));
            await ProducePackageAsync(settings, pending.TargetUtc, plan, ct, budget.Token, pending.Id);
            return true;
        }
    }

    public async Task<NewsPackage?> ProduceNextPackageAsync(CancellationToken ct)
    {
        StationSettings settings;
        PackagePlan plan;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            plan = ResolveNextPackagePlan(settings, timeProvider.GetLocalNow());
            var targetUtc = plan.TargetLocal.UtcDateTime;

            var existing = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc == targetUtc
                    && package.Status != NewsPackageStatus.Failed)
                .OrderByDescending(package => package.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        return await ProducePackageAsync(settings, plan.TargetLocal.UtcDateTime, plan, ct, budget.Token);
    }

    public async Task<NewsPackage?> RecreatePackageAsync(Guid packageId, CancellationToken ct)
    {
        StationSettings settings;
        DateTime targetUtc;
        PackagePlan plan;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            var package = await db.NewsPackages.FirstOrDefaultAsync(candidate => candidate.Id == packageId, ct)
                ?? throw new KeyNotFoundException("News package was not found.");
            if (package.Status is NewsPackageStatus.Queued or NewsPackageStatus.Played)
            {
                throw new InvalidOperationException("Queued or played packages cannot be recreated.");
            }

            targetUtc = package.TargetUtc;
            plan = BuildPackagePlan(settings, ToLocalTime(targetUtc, timeProvider.GetLocalNow().Offset));
            foreach (var item in await db.NewsItems
                .Where(item => item.Status == NewsItemStatus.Selected
                    && item.SelectionReason == "Top-of-hour package")
                .ToListAsync(ct))
            {
                item.Status = NewsItemStatus.New;
                item.SelectionReason = null;
            }

            await db.SaveChangesAsync(ct);
        }

        await productionUpdates.PublishNewsChangedAsync(ct);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        return await ProducePackageAsync(settings, targetUtc, plan, ct, budget.Token, packageId);
    }

    private async Task<NewsPackage?> ProducePackageAsync(
        StationSettings settings,
        DateTime targetUtc,
        PackagePlan plan,
        CancellationToken stoppingToken,
        CancellationToken ct,
        Guid? reusePackageId = null)
    {
        var includeNews = plan.IncludeNews;
        var includeWeather = plan.IncludeWeather;
        var weatherOnly = !includeNews && includeWeather;
        List<NewsItem> items = [];
        NewsPackage package;
        var step = weatherOnly ? "creating package" : "starting package";
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (reusePackageId is { } packageId)
            {
                package = await db.NewsPackages.FirstOrDefaultAsync(candidate => candidate.Id == packageId, ct)
                    ?? throw new KeyNotFoundException("News package was not found.");
                package.Kind = NewsPackageKind.TopOfHour;
                package.Status = NewsPackageStatus.Pending;
                package.TargetUtc = targetUtc;
                package.TargetDurationSeconds = TargetDurationSeconds(settings, plan);
                package.AnnouncementId = null;
                package.ProducedAtUtc = null;
                package.QueuedAtUtc = null;
                package.PlayedAtUtc = null;
                package.FailureReason = null;
                package.ProductionState = "Starting package production.";
                package.SourceSummary = null;
            }
            else
            {
                package = new NewsPackage
                {
                    Id = Guid.NewGuid(),
                    Kind = NewsPackageKind.TopOfHour,
                    Status = NewsPackageStatus.Pending,
                    TargetUtc = targetUtc,
                    TargetDurationSeconds = TargetDurationSeconds(settings, plan),
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                    ProductionState = "Starting package production.",
                };
                db.NewsPackages.Add(package);
            }
            await db.SaveChangesAsync(ct);
        }
        await productionUpdates.PublishNewsChangedAsync(ct);

        try
        {
            if (includeNews)
            {
                step = "fetching news";
                await UpdatePackageProductionStateAsync(package.Id, "Fetching news.", ct);
                await feedPolling.PollEnabledFeedsAsync(ct);

                step = "selecting news items";
                await UpdatePackageProductionStateAsync(package.Id, "Selecting news items.", ct);
                await using var itemDb = await dbFactory.CreateDbContextAsync(ct);
                var candidates = await itemDb.NewsItems
                    .Include(item => item.Feed)
                    .Where(item => item.Status == NewsItemStatus.New
                        && item.Feed != null
                        && item.Feed.IsEnabled)
                    .ToListAsync(ct);
                items = NewsCategoryOrdering
                    .SortItems(candidates, NewsCategoryOrdering.Parse(settings.NewsCategoryOrder))
                    .Take(5)
                    .ToList();
                if (items.Count == 0 && !includeWeather)
                {
                    await MarkPackageFailedAsync(package.Id, "Production stopped: no news items are available.", items, ct);
                    return await LoadPackageAsync(package.Id, CancellationToken.None);
                }

                foreach (var item in items)
                {
                    item.Status = NewsItemStatus.Selected;
                    item.SelectionReason = "Top-of-hour package";
                }

                await itemDb.SaveChangesAsync(ct);
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            step = "creating production scope";
            await UpdatePackageProductionStateAsync(package.Id, "Preparing package production.", ct);
            using var scope = scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();
            var extractor = scope.ServiceProvider.GetRequiredService<INewsArticleExtractor>();
            var weatherSource = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();
            var specialistHosts = scope.ServiceProvider.GetRequiredService<SpecialistHostCreationService>();
            step = "loading current show context";
            await UpdatePackageProductionStateAsync(package.Id, "Loading show context.", ct);
            var context = await schedule.GetCurrentAsync(ct);

            var expiresAt = targetUtc.AddMinutes(15);
            var targetEnd = targetUtc.AddSeconds(
                TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
            var localNow = timeProvider.GetLocalNow();
            var targetLocal = new DateTimeOffset(
                DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc),
                TimeSpan.Zero).ToOffset(localNow.Offset);
            Moderator? newsModerator = null;
            IReadOnlyList<Announcement> newsParts = [];
            if (includeNews && items.Count > 0)
            {
                step = "producing news block";
                var producedNews = await TryProduceBlockAsync(
                    package.Id,
                    "news block",
                    items,
                    async token =>
                    {
                        await UpdatePackageProductionStateAsync(package.Id, "Resolving news specialist.", token);
                        var moderator = await ResolveNewsModeratorAsync(settings, specialistHosts, token);
                        await UpdatePackageProductionStateAsync(package.Id, "Extracting article text.", token);
                        await EnrichItemsAsync(items, settings.NewsExtractionEnabled, extractor, token);
                        var handoff = BuildIntroText(context.Moderator, moderator, targetLocal);
                        await UpdatePackageProductionStateAsync(package.Id, "Writing news script.", token);
                        var draft = await factory.WriteScriptDraftAsync(
                            AnnouncementKind.News,
                            moderator,
                            null,
                            BuildNewsFacts(items, targetLocal),
                            settings.StationName,
                            token,
                            lengthHint: $"A top-of-hour bulletin of up to {Math.Max(1, package.TargetDurationSeconds / 60)} minutes. Cover each item briefly and clearly.",
                            alreadySpokenContext: handoff,
                            localNowOverride: targetLocal,
                            priority: PromptPriority.High);

                        var produced = new List<Announcement>();
                        await UpdatePackageProductionStateAsync(package.Id, "Recording top-of-hour handoff.", token);
                        produced.Add(await factory.ProduceDirectAsync(
                            AnnouncementKind.StationId,
                            TalkPartKind.StationId,
                            TalkBreakPriority.High,
                            context.Moderator,
                            handoff,
                            "TopOfHourHandoff",
                            token,
                            title: "Top of hour",
                            expiresAtUtc: expiresAt,
                            desiredDurationSeconds: 8,
                            wordBudget: 22));
                        await UpdatePackageProductionStateAsync(package.Id, "Recording news bulletin.", token);
                        produced.Add(await factory.ProduceFromDraftAsync(draft, token));

                        return new ProducedNewsBlock(moderator, produced);
                    },
                    ct);
                if (producedNews is not null)
                {
                    newsModerator = producedNews.Moderator;
                    newsParts = producedNews.Parts;
                }
            }

            Moderator? weatherModerator = null;
            IReadOnlyList<Announcement> weatherParts = [];
            if (includeWeather)
            {
                step = "producing weather block";
                var producedWeather = await TryProduceBlockAsync(
                    package.Id,
                    "weather block",
                    items,
                    async token =>
                    {
                        await UpdatePackageProductionStateAsync(package.Id, "Resolving weather specialist.", token);
                        var moderator = await ResolveWeatherModeratorAsync(settings, newsModerator, specialistHosts, token);
                        var handoff = $"{moderator.Name} has the weather.";
                        await UpdatePackageProductionStateAsync(package.Id, "Loading weather report.", token);
                        var report = await weatherSource.GetReportAsync(moderator.Language, token);
                        await UpdatePackageProductionStateAsync(package.Id, "Writing weather script.", token);
                        var draft = await factory.WriteScriptDraftAsync(
                            AnnouncementKind.Weather,
                            moderator,
                            null,
                            report.ToFacts(targetLocal.DateTime),
                            settings.StationName,
                            token,
                            lengthHint: "A concise weather report, about 45 seconds.",
                            alreadySpokenContext: handoff,
                            localNowOverride: targetLocal,
                            priority: PromptPriority.High);

                        var produced = new List<Announcement>();
                        if (newsModerator is not null)
                        {
                            await UpdatePackageProductionStateAsync(package.Id, "Recording weather handoff.", token);
                            produced.Add(await factory.ProduceDirectAsync(
                                AnnouncementKind.StationId,
                                TalkPartKind.WeatherHandoff,
                                TalkBreakPriority.High,
                                newsModerator,
                                handoff,
                                "WeatherHandoff",
                                token,
                                title: "Weather handoff",
                                expiresAtUtc: expiresAt,
                                desiredDurationSeconds: 5,
                                wordBudget: 14));
                        }

                        await UpdatePackageProductionStateAsync(package.Id, "Recording weather forecast.", token);
                        produced.Add(await factory.ProduceFromDraftAsync(draft, token));
                        return new ProducedWeatherBlock(moderator, produced);
                    },
                    ct);
                if (producedWeather is not null)
                {
                    weatherModerator = producedWeather.Moderator;
                    weatherParts = producedWeather.Parts;
                }
            }

            var parts = newsParts.Concat(weatherParts).ToList();
            if (parts.Count == 0)
            {
                await MarkPackageFailedAsync(package.Id, "Production stopped: no package audio could be produced.", items, ct);
                return await LoadPackageAsync(package.Id, CancellationToken.None);
            }

            var producedItems = newsParts.Count > 0 ? items : [];
            var producedPlan = plan with
            {
                IncludeNews = newsParts.Count > 0,
                IncludeWeather = weatherParts.Count > 0,
            };
            step = "marking package announcements as scheduled";
            await UpdatePackageProductionStateAsync(package.Id, "Scheduling package audio.", ct);
            await MarkScheduledAsync(parts.Select(part => part.Id).ToList(), targetUtc, targetEnd, expiresAt, ct);
            step = "rendering package audio";
            await UpdatePackageProductionStateAsync(package.Id, "Rendering package audio.", ct);
            var fallbackModerator = newsModerator ?? weatherModerator ?? context.Moderator;
            var composite = parts.Count == 1 ? parts[0] : await renderer.RenderAsync(parts, fallbackModerator, ct);
            step = "finalizing package";
            await UpdatePackageProductionStateAsync(package.Id, "Finalizing package.", ct);
            await FinalizePackageAsync(package.Id, composite, targetUtc, targetEnd, expiresAt, producedItems, producedPlan, ct);

            logger.LogInformation(
                "Scheduled package ready for {Target:u}: {Count} news item(s), weather {IncludeWeather}, announcement {AnnouncementId}",
                targetUtc,
                items.Count,
                includeWeather,
                composite.Id);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
        catch (Exception ex) when (IsCancellationLikeFailure(ex, stoppingToken, ct))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                await MarkPackageStoppedAsync(
                    package.Id,
                    $"Production stopped during shutdown while {step}.",
                    items,
                    CancellationToken.None);
                logger.LogInformation(
                    "Top-of-hour package production stopped during shutdown for {Target:u}",
                    targetUtc);
                return await LoadPackageAsync(package.Id, CancellationToken.None);
            }

            metrics.GenerationFailed("news");
            await MarkPackageStoppedAsync(
                package.Id,
                IsAbortedIoFailure(ex)
                    ? $"Production stopped during {step}: {FailureDetail(ex)}"
                    : $"Production timed out or was cancelled during {step}: {FailureDetail(ex)}",
                items,
                CancellationToken.None);
            if (IsAbortedIoFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Top-of-hour package production aborted during {Step} for {Target:u}: {Message}",
                    step,
                    targetUtc,
                    ex.GetBaseException().Message);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "Top-of-hour package production timed out during {Step} for {Target:u}",
                    step,
                    targetUtc);
            }

            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.GenerationFailed("news");
            await MarkPackageStoppedAsync(package.Id, $"Production stopped during {step}: {FailureDetail(ex)}", items, ct);
            logger.LogWarning(
                ex,
                "Top-of-hour package production failed during {Step} for {Target:u}: {Message}",
                step,
                targetUtc,
                ex.GetBaseException().Message);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
    }

    private async Task<T?> TryProduceBlockAsync<T>(
        Guid packageId,
        string blockName,
        IReadOnlyList<NewsItem> items,
        Func<CancellationToken, Task<T>> produce,
        CancellationToken ct)
        where T : class
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= BlockMaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    logger.LogInformation(
                        "Retrying top-of-hour {Block} ({Attempt}/{MaxAttempts})",
                        blockName,
                        attempt,
                        BlockMaxAttempts);
                }

                return await produce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                metrics.GenerationFailed("news");
                await MarkPackageRetryPendingAsync(
                    packageId,
                    $"Production failed during {blockName} attempt {attempt}/{BlockMaxAttempts}: {FailureDetail(ex)}",
                    items,
                    CancellationToken.None);
                logger.LogWarning(
                    ex,
                    "Top-of-hour {Block} failed on attempt {Attempt}/{MaxAttempts}: {Message}",
                    blockName,
                    attempt,
                    BlockMaxAttempts,
                    ex.GetBaseException().Message);

                if (attempt < BlockMaxAttempts)
                {
                    await Task.Delay(BlockRetryDelay, ct);
                }
            }
        }

        logger.LogWarning(
            "Top-of-hour {Block} skipped after {MaxAttempts} failed attempt(s): {Message}",
            blockName,
            BlockMaxAttempts,
            last?.GetBaseException().Message);
        return null;
    }

    private async Task<NewsPackage?> LoadPackageAsync(Guid packageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NewsPackages.AsNoTracking().FirstOrDefaultAsync(package => package.Id == packageId, ct);
    }

    private async Task EnrichItemsAsync(
        IEnumerable<NewsItem> items,
        bool extractionEnabled,
        INewsArticleExtractor extractor,
        CancellationToken ct)
    {
        if (!extractionEnabled)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var skipped = 0;
        foreach (var item in items.Where(item => string.IsNullOrWhiteSpace(item.ExtractedSummary)))
        {
            string? extracted;
            try
            {
                extracted = await extractor.ExtractAsync(item.Url, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                skipped++;
                logger.LogDebug(
                    ex,
                    "News article extraction skipped for {Title} ({Url}): {Message}",
                    item.Title,
                    item.Url,
                    ex.GetBaseException().Message);
                continue;
            }

            if (string.IsNullOrWhiteSpace(extracted))
            {
                continue;
            }

            await db.NewsItems
                .Where(candidate => candidate.Id == item.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    candidate => candidate.ExtractedSummary,
                    extracted.Length <= 2000 ? extracted : extracted[..2000]), ct);
            item.ExtractedSummary = extracted.Length <= 2000 ? extracted : extracted[..2000];
        }

        if (skipped > 0)
        {
            logger.LogInformation(
                "News article extraction skipped {Count} external page(s); using feed summaries for those items",
                skipped);
        }
    }

    private async Task<Moderator> ResolveNewsModeratorAsync(
        StationSettings settings,
        SpecialistHostCreationService specialistHosts,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        var resolved = ProductionSpecialistPolicy.ResolveNewsModerator(settings, moderators);
        if (resolved is not null)
        {
            return resolved;
        }

        logger.LogInformation("No active news specialist found; program director will create one for this package");
        return await specialistHosts.CreateAsync(
            SpecialistHostRole.News,
            "Create a top-of-hour news anchor because no active news specialist is available for this station.",
            ct);
    }

    private async Task<Moderator> ResolveWeatherModeratorAsync(
        StationSettings settings,
        Moderator? newsModerator,
        SpecialistHostCreationService specialistHosts,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        var resolved = ProductionSpecialistPolicy.ResolveWeatherModerator(
            settings,
            moderators,
            newsModerator ?? new Moderator { Id = int.MinValue });
        if (resolved is not null)
        {
            return resolved;
        }

        logger.LogInformation(
            "No distinct active weather specialist found; program director will create one for this package");
        return await specialistHosts.CreateAsync(
            SpecialistHostRole.Weather,
            "Create a weather specialist for top-of-hour forecasts because no distinct active weather specialist is available.",
            ct);
    }

    private async Task MarkScheduledAsync(
        IReadOnlyList<Guid> announcementIds,
        DateTime targetUtc,
        DateTime targetEndUtc,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var breaks = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.AnnouncementId != null
                && announcementIds.Contains(talkBreak.AnnouncementId.Value))
            .ToListAsync(ct);
        foreach (var talkBreak in breaks)
        {
            talkBreak.Priority = TalkBreakPriority.Scheduled;
            talkBreak.TargetWindowStartUtc = targetUtc;
            talkBreak.TargetWindowEndUtc = targetEndUtc;
            talkBreak.ExpiresAtUtc = expiresAtUtc;
            foreach (var part in talkBreak.Parts)
            {
                part.Priority = TalkBreakPriority.Scheduled;
                part.TargetWindowStartUtc = targetUtc;
                part.TargetWindowEndUtc = targetEndUtc;
                part.ExpiresAtUtc = expiresAtUtc;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task FinalizePackageAsync(
        Guid packageId,
        Announcement composite,
        DateTime targetUtc,
        DateTime targetEndUtc,
        DateTime expiresAtUtc,
        IReadOnlyList<NewsItem> items,
        PackagePlan plan,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstAsync(p => p.Id == packageId, ct);
        package.Status = NewsPackageStatus.Ready;
        package.AnnouncementId = composite.Id;
        package.ProducedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        package.FailureReason = null;
        package.ProductionState = null;
        package.SourceSummary = !plan.IncludeNews && plan.IncludeWeather
            ? "Weather forecast"
            : string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"));

        var announcement = await db.Announcements.FirstAsync(a => a.Id == composite.Id, ct);
        announcement.Kind = plan.IncludeNews ? AnnouncementKind.News : AnnouncementKind.Weather;
        announcement.PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly;

        var talkBreak = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .FirstAsync(talkBreak => talkBreak.AnnouncementId == composite.Id, ct);
        talkBreak.Priority = TalkBreakPriority.Scheduled;
        talkBreak.Purpose = plan.IncludeNews ? "TopOfHourPackage" : "WeatherReport";
        talkBreak.Title = plan.IncludeNews ? "Top of hour" : "Weather";
        talkBreak.TargetWindowStartUtc = targetUtc;
        talkBreak.TargetWindowEndUtc = targetEndUtc;
        talkBreak.ExpiresAtUtc = expiresAtUtc;
        foreach (var part in talkBreak.Parts)
        {
            part.Priority = TalkBreakPriority.Scheduled;
            part.TargetWindowStartUtc = targetUtc;
            part.TargetWindowEndUtc = targetEndUtc;
            part.ExpiresAtUtc = expiresAtUtc;
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            item.Status = NewsItemStatus.Produced;
            item.ProducedAtUtc = package.ProducedAtUtc;
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task UpdatePackageProductionStateAsync(
        Guid packageId,
        string state,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null || package.Status is not (NewsPackageStatus.Pending or NewsPackageStatus.Retrying))
        {
            return;
        }

        package.Status = NewsPackageStatus.Pending;
        package.ProductionState = state.Length <= 500 ? state : state[..500];
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task MarkPackageFailedAsync(
        Guid packageId,
        string reason,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is not null)
        {
            package.Status = NewsPackageStatus.Failed;
            package.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
            package.ProductionState = null;
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            if (item.Status == NewsItemStatus.Selected)
            {
                item.Status = NewsItemStatus.New;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task MarkPackageStoppedAsync(
        Guid packageId,
        string reason,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is not null && package.Status is NewsPackageStatus.Pending or NewsPackageStatus.Retrying)
        {
            package.Status = NewsPackageStatus.Retrying;
            package.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
            package.ProductionState = "Stopped. Waiting for the production service to retry.";
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            if (item.Status == NewsItemStatus.Selected)
            {
                item.Status = NewsItemStatus.New;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task MarkPackageRetryPendingAsync(
        Guid packageId,
        string reason,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is not null && package.Status is NewsPackageStatus.Pending or NewsPackageStatus.Retrying)
        {
            package.Status = NewsPackageStatus.Retrying;
            package.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
            package.ProductionState = "Retrying after a failed production block.";
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            if (item.Status == NewsItemStatus.Selected)
            {
                item.Status = NewsItemStatus.New;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    internal static string BuildIntroText(Moderator currentHost, Moderator newsModerator, DateTimeOffset localNow)
    {
        var timeText = localNow.ToString("HH:mm");
        return currentHost.Id == newsModerator.Id
            ? $"It's {timeText}. Here is the news."
            : $"It's {timeText}. {newsModerator.Name} has the news.";
    }

    internal static DateTimeOffset ResolveNextPackageTarget(StationSettings settings, DateTimeOffset localNow)
        => ResolveNextPackagePlan(settings, localNow).TargetLocal;

    internal static PackagePlan? ResolveNextPreparationPlan(StationSettings settings, DateTimeOffset localNow)
    {
        var plan = ResolveNextPackagePlan(settings, localNow);
        return plan.TargetLocal - localNow <= TimeSpan.FromMinutes(TopOfHourScheduler.DefaultPrepareAheadMinutes)
            ? plan
            : null;
    }

    internal static PackagePlan ResolveNextPackagePlan(StationSettings settings, DateTimeOffset localNow)
    {
        var newsTarget = TopOfHourScheduler.NextTarget(localNow, settings.NewsPackageCadenceMinutes);
        if (!settings.WeatherEnabled)
        {
            return BuildPackagePlan(settings, newsTarget);
        }

        if (!settings.NewsEnabled)
        {
            return BuildPackagePlan(settings, WeatherScheduler.NextWindowStart(localNow, settings.WeatherCadenceMinutes));
        }

        return BuildPackagePlan(settings, newsTarget);
    }

    internal static PackagePlan BuildPackagePlan(StationSettings settings, DateTimeOffset targetLocal)
    {
        var includeNews = settings.NewsEnabled
            && IsCadenceBoundary(targetLocal, TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes));
        var includeWeather = settings.WeatherEnabled
            && IsCadenceBoundary(targetLocal, WeatherScheduler.NormalizeCadence(settings.WeatherCadenceMinutes));
        return new PackagePlan(targetLocal, includeNews, includeWeather);
    }

    private static int TargetDurationSeconds(StationSettings settings, PackagePlan plan)
        => !plan.IncludeNews && plan.IncludeWeather
            ? 60
            : Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60);

    private static bool IsCadenceBoundary(DateTimeOffset localTime, int cadenceMinutes)
    {
        var minuteOfDay = localTime.Hour * 60 + localTime.Minute;
        return minuteOfDay % cadenceMinutes == 0;
    }

    private static DateTimeOffset ToLocalTime(DateTime targetUtc, TimeSpan localOffset)
        => new DateTimeOffset(DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc), TimeSpan.Zero).ToOffset(localOffset);

    internal static string BuildNewsFacts(IEnumerable<NewsItem> items, DateTimeOffset localNow)
        => $"Bulletin time: {localNow:yyyy-MM-dd HH:mm} local.\n\n" + string.Join(
            "\n\n",
            items.Select((item, index) =>
                $"{index + 1}. Source: {item.Feed?.Label ?? "Unknown"}\n"
                + $"Category: {item.Feed?.Category ?? "general"}\n"
                + $"Title: {item.Title}\n"
                + $"Published UTC: {item.PublishedAtUtc:O}\n"
                + $"Summary/source text: {FirstNonEmpty(item.ExtractedSummary, item.Summary, "No summary available.")}\n"
                + $"URL: {item.Url}"));

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsCancellationLikeFailure(
        Exception ex,
        CancellationToken stoppingToken,
        CancellationToken productionToken)
    {
        if (IsAbortedIoFailure(ex))
        {
            return true;
        }

        if (!stoppingToken.IsCancellationRequested && !productionToken.IsCancellationRequested)
        {
            return false;
        }

        var root = ex.GetBaseException();
        return ex is OperationCanceledException or TaskCanceledException or IOException or HttpRequestException
            || root is OperationCanceledException or TaskCanceledException or IOException or HttpRequestException;
    }

    private static bool IsAbortedIoFailure(Exception ex)
    {
        var root = ex.GetBaseException();
        return root is IOException
            && (root.Message.Contains("operation has been aborted", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("E/A-Vorgang wurde", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("Threadendes", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("Anwendungsanforderung", StringComparison.OrdinalIgnoreCase));
    }

    private static string FailureDetail(Exception ex)
    {
        var root = ex.GetBaseException();
        var message = string.IsNullOrWhiteSpace(root.Message) ? ex.Message : root.Message;
        var detail = root.GetType() == ex.GetType()
            ? $"{root.GetType().Name}: {message}"
            : $"{ex.GetType().Name}/{root.GetType().Name}: {message}";
        return detail.Length <= 800 ? detail : detail[..800];
    }

    private sealed record ProducedNewsBlock(Moderator Moderator, IReadOnlyList<Announcement> Parts);

    private sealed record ProducedWeatherBlock(Moderator Moderator, IReadOnlyList<Announcement> Parts);
}
