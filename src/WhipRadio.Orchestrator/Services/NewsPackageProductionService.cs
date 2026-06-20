using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class NewsPackageProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    NewsFeedPollingService feedPolling,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    ILogger<NewsPackageProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProductionBudget = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "News package production cycle failed");
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        StationSettings settings;
        DateTimeOffset targetLocal;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!settings.NewsEnabled)
            {
                return;
            }

            targetLocal = TopOfHourScheduler.NextPreparationTarget(
                timeProvider.GetLocalNow(),
                settings.NewsPackageCadenceMinutes);
            if (targetLocal == DateTimeOffset.MinValue)
            {
                return;
            }

            var targetUtc = targetLocal.UtcDateTime;
            if (await db.NewsPackages.AsNoTracking()
                .AnyAsync(package => package.Kind == NewsPackageKind.TopOfHour && package.TargetUtc == targetUtc, ct))
            {
                return;
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        await ProducePackageAsync(settings, targetLocal.UtcDateTime, ct, budget.Token);
    }

    public async Task<NewsPackage?> ProduceNextPackageAsync(CancellationToken ct)
    {
        StationSettings settings;
        DateTime targetUtc;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            targetUtc = TopOfHourScheduler.NextTarget(
                timeProvider.GetLocalNow(),
                settings.NewsPackageCadenceMinutes).UtcDateTime;

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
        return await ProducePackageAsync(settings, targetUtc, ct, budget.Token);
    }

    public async Task<NewsPackage?> RecreatePackageAsync(Guid packageId, CancellationToken ct)
    {
        StationSettings settings;
        DateTime targetUtc;
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
        return await ProducePackageAsync(settings, targetUtc, ct, budget.Token, packageId);
    }

    private async Task<NewsPackage?> ProducePackageAsync(
        StationSettings settings,
        DateTime targetUtc,
        CancellationToken stoppingToken,
        CancellationToken ct,
        Guid? reusePackageId = null)
    {
        var step = "polling feeds";
        await feedPolling.PollEnabledFeedsAsync(ct);

        List<NewsItem> items;
        NewsPackage package;
        step = "selecting news items";
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var candidates = await db.NewsItems
                .Include(item => item.Feed)
                .Where(item => item.Status == NewsItemStatus.New
                    && item.Feed != null
                    && item.Feed.IsEnabled)
                .ToListAsync(ct);
            items = NewsCategoryOrdering
                .SortItems(candidates, NewsCategoryOrdering.Parse(settings.NewsCategoryOrder))
                .Take(5)
                .ToList();
            if (items.Count == 0)
            {
                return null;
            }

            if (reusePackageId is { } packageId)
            {
                package = await db.NewsPackages.FirstOrDefaultAsync(candidate => candidate.Id == packageId, ct)
                    ?? throw new KeyNotFoundException("News package was not found.");
                package.Kind = NewsPackageKind.TopOfHour;
                package.Status = NewsPackageStatus.Pending;
                package.TargetUtc = targetUtc;
                package.TargetDurationSeconds = Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60);
                package.AnnouncementId = null;
                package.ProducedAtUtc = null;
                package.QueuedAtUtc = null;
                package.PlayedAtUtc = null;
                package.FailureReason = null;
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
                    TargetDurationSeconds = Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60),
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                };
                db.NewsPackages.Add(package);
            }

            foreach (var item in items)
            {
                item.Status = NewsItemStatus.Selected;
                item.SelectionReason = "Top-of-hour package";
            }

            await db.SaveChangesAsync(ct);
        }
        await productionUpdates.PublishNewsChangedAsync(ct);

        try
        {
            step = "creating production scope";
            using var scope = scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();
            var extractor = scope.ServiceProvider.GetRequiredService<INewsArticleExtractor>();
            var weatherSource = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();
            var specialistHosts = scope.ServiceProvider.GetRequiredService<SpecialistHostCreationService>();
            step = "loading current show context";
            var context = await schedule.GetCurrentAsync(ct);

            step = "resolving news specialist";
            var newsModerator = await ResolveNewsModeratorAsync(settings, specialistHosts, ct);
            step = "extracting article text";
            await EnrichItemsAsync(items, settings.NewsExtractionEnabled, extractor, ct);
            var expiresAt = targetUtc.AddMinutes(15);
            var targetEnd = targetUtc.AddSeconds(
                TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
            var localNow = timeProvider.GetLocalNow();
            var newsHandoff = BuildIntroText(context.Moderator, newsModerator, localNow);

            step = "writing news script";
            var newsDraft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.News,
                newsModerator,
                null,
                BuildNewsFacts(items, localNow),
                settings.StationName,
                ct,
                lengthHint: $"A top-of-hour bulletin of up to {Math.Max(1, package.TargetDurationSeconds / 60)} minutes. Cover each item briefly and clearly.",
                alreadySpokenContext: newsHandoff);

            Moderator? weatherModerator = null;
            string? weatherHandoff = null;
            AnnouncementFactory.AnnouncementScriptDraft? weatherDraft = null;
            if (settings.WeatherEnabled)
            {
                step = "resolving weather specialist";
                weatherModerator = await ResolveWeatherModeratorAsync(settings, newsModerator, specialistHosts, ct);
                weatherHandoff = $"{weatherModerator.Name} has the weather.";

                step = "loading weather report";
                var report = await weatherSource.GetReportAsync(weatherModerator.Language, ct);
                step = "writing weather script";
                weatherDraft = await factory.WriteScriptDraftAsync(
                    AnnouncementKind.Weather,
                    weatherModerator,
                    null,
                    report.ToFacts(),
                    settings.StationName,
                    ct,
                    lengthHint: "A concise weather report, about 45 seconds.",
                    alreadySpokenContext: weatherHandoff);
            }

            step = "recording news handoff";
            var parts = new List<Announcement>
            {
                await factory.ProduceDirectAsync(
                    AnnouncementKind.StationId,
                    TalkPartKind.StationId,
                    TalkBreakPriority.Scheduled,
                    context.Moderator,
                    newsHandoff,
                    "TopOfHourHandoff",
                    ct,
                    title: "Top of hour",
                    expiresAtUtc: expiresAt,
                    desiredDurationSeconds: 8,
                    wordBudget: 22),
            };

            step = "directing and recording news bulletin";
            parts.Add(await factory.ProduceFromDraftAsync(newsDraft, ct));

            if (weatherDraft is not null && weatherModerator is not null && weatherHandoff is not null)
            {
                step = "recording weather handoff";
                parts.Add(await factory.ProduceDirectAsync(
                    AnnouncementKind.StationId,
                    TalkPartKind.WeatherHandoff,
                    TalkBreakPriority.Scheduled,
                    newsModerator,
                    weatherHandoff,
                    "WeatherHandoff",
                    ct,
                    title: "Weather handoff",
                    expiresAtUtc: expiresAt,
                    desiredDurationSeconds: 5,
                    wordBudget: 14));

                step = "directing and recording weather forecast";
                parts.Add(await factory.ProduceFromDraftAsync(weatherDraft, ct));
            }

            step = "marking package announcements as scheduled";
            await MarkScheduledAsync(parts.Select(part => part.Id).ToList(), targetUtc, targetEnd, expiresAt, ct);
            step = "rendering package audio";
            var composite = parts.Count == 1 ? parts[0] : await renderer.RenderAsync(parts, newsModerator, ct);
            step = "finalizing package";
            await FinalizePackageAsync(package.Id, composite, targetUtc, targetEnd, expiresAt, items, ct);

            logger.LogInformation(
                "Top-of-hour package ready for {Target:u}: {Count} news item(s), announcement {AnnouncementId}",
                targetUtc,
                items.Count,
                composite.Id);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
        catch (Exception ex) when (IsCancellationLikeFailure(ex, stoppingToken, ct))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Top-of-hour package production cancelled during shutdown for {Target:u}",
                    targetUtc);
                return await LoadPackageAsync(package.Id, CancellationToken.None);
            }

            await MarkPackageFailedAsync(
                package.Id,
                IsAbortedIoFailure(ex)
                    ? $"Production was aborted during {step}: {FailureDetail(ex)}"
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
            await MarkPackageFailedAsync(package.Id, $"Production failed during {step}: {FailureDetail(ex)}", items, ct);
            logger.LogWarning(
                ex,
                "Top-of-hour package production failed during {Step} for {Target:u}: {Message}",
                step,
                targetUtc,
                ex.GetBaseException().Message);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
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
        Moderator newsModerator,
        SpecialistHostCreationService specialistHosts,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        var resolved = ProductionSpecialistPolicy.ResolveWeatherModerator(settings, moderators, newsModerator);
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
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstAsync(p => p.Id == packageId, ct);
        package.Status = NewsPackageStatus.Ready;
        package.AnnouncementId = composite.Id;
        package.ProducedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        package.SourceSummary = string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"));

        var announcement = await db.Announcements.FirstAsync(a => a.Id == composite.Id, ct);
        announcement.Kind = AnnouncementKind.News;

        var talkBreak = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .FirstAsync(talkBreak => talkBreak.AnnouncementId == composite.Id, ct);
        talkBreak.Priority = TalkBreakPriority.Scheduled;
        talkBreak.Purpose = "TopOfHourPackage";
        talkBreak.Title = "Top of hour";
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
}
