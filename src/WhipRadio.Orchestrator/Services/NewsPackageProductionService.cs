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
    private static readonly TimeSpan ProductionBudget = TimeSpan.FromMinutes(6);

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
        await ProducePackageAsync(settings, targetLocal.UtcDateTime, budget.Token);
    }

    private async Task ProducePackageAsync(StationSettings settings, DateTime targetUtc, CancellationToken ct)
    {
        await feedPolling.PollEnabledFeedsAsync(ct);

        List<NewsItem> items;
        NewsPackage package;
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
                return;
            }

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
            using var scope = scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();
            var extractor = scope.ServiceProvider.GetRequiredService<INewsArticleExtractor>();
            var weatherSource = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();
            var context = await schedule.GetCurrentAsync(ct);

            var newsModerator = await ResolveNewsModeratorAsync(settings, context.Moderator, ct);
            await EnrichItemsAsync(items, settings.NewsExtractionEnabled, extractor, ct);
            var facts = BuildNewsFacts(items);
            var expiresAt = targetUtc.AddMinutes(15);
            var targetEnd = targetUtc.AddSeconds(TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds));
            var newsHandoff = BuildIntroText(context.Moderator, newsModerator);

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
                await factory.ProduceAsync(
                    AnnouncementKind.News,
                    newsModerator,
                    null,
                    facts,
                    settings.StationName,
                    ct,
                    lengthHint: $"A top-of-hour bulletin of up to {Math.Max(1, package.TargetDurationSeconds / 60)} minutes. Cover each item briefly and clearly.",
                    alreadySpokenContext: newsHandoff),
            };

            if (settings.WeatherEnabled)
            {
                var weatherModerator = await ResolveWeatherModeratorAsync(settings, newsModerator, ct);
                if (weatherModerator is null)
                {
                    logger.LogWarning(
                        "Weather skipped for top-of-hour package {PackageId}: no distinct active weather specialist",
                        package.Id);
                }
                else
                {
                    var weatherHandoff = $"{weatherModerator.Name} has the weather.";
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

                    var report = await weatherSource.GetReportAsync(weatherModerator.Language, ct);
                    parts.Add(await factory.ProduceAsync(
                        AnnouncementKind.Weather,
                        weatherModerator,
                        null,
                        report.ToFacts(),
                        settings.StationName,
                        ct,
                        lengthHint: "A concise weather report, about 45 seconds.",
                        alreadySpokenContext: weatherHandoff));
                }
            }

            await MarkScheduledAsync(parts.Select(part => part.Id).ToList(), targetUtc, targetEnd, expiresAt, ct);
            var composite = parts.Count == 1 ? parts[0] : await renderer.RenderAsync(parts, newsModerator, ct);
            await FinalizePackageAsync(package.Id, composite, targetUtc, targetEnd, expiresAt, items, ct);

            logger.LogInformation(
                "Top-of-hour package ready for {Target:u}: {Count} news item(s), announcement {AnnouncementId}",
                targetUtc,
                items.Count,
                composite.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await MarkPackageFailedAsync(package.Id, ex.GetBaseException().Message, items, ct);
            logger.LogWarning(ex, "Top-of-hour package production failed for {Target:u}", targetUtc);
        }
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
        foreach (var item in items.Where(item => string.IsNullOrWhiteSpace(item.ExtractedSummary)))
        {
            var extracted = await extractor.ExtractAsync(item.Url, ct);
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
    }

    private async Task<Moderator> ResolveNewsModeratorAsync(
        StationSettings settings,
        Moderator fallback,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        return ProductionSpecialistPolicy.ResolveNewsModerator(settings, moderators, fallback);
    }

    private async Task<Moderator?> ResolveWeatherModeratorAsync(
        StationSettings settings,
        Moderator newsModerator,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        return ProductionSpecialistPolicy.ResolveWeatherModerator(settings, moderators, newsModerator);
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

    private static string BuildIntroText(Moderator currentHost, Moderator newsModerator)
        => currentHost.Id == newsModerator.Id
            ? "Top of the hour. Here is the news."
            : $"Top of the hour. {newsModerator.Name} has the news.";

    private static string BuildNewsFacts(IEnumerable<NewsItem> items)
        => string.Join(
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
}
