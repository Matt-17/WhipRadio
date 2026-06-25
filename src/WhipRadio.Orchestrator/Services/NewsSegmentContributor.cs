using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// News segment contributor for top-of-hour packages. Produces an LLM-written
/// intro handover (with direct-text fallback) and a news bulletin from RSS
/// feeds. When no items are available or the bulletin fails after retries,
/// the intro still airs with a short gap line and a degradation reason.
/// </summary>
public sealed class NewsSegmentContributor(
    IDbContextFactory<RadioDbContext> dbFactory,
    NewsFeedPollingService feedPolling,
    IStationMetrics metrics,
    ILogger<NewsSegmentContributor> logger) : ITopOfHourSegmentContributor
{
    private const int BlockMaxAttempts = 3;

    /// <summary>How many candidate stories to hand the bulletin writer. We pass more than
    /// will air: the writer curates — leading with the strongest, merging duplicates, and
    /// dropping weak items — so it needs a real pool to pick from, not a pre-trimmed five.</summary>
    private const int MaxCandidateItems = 8;

    private static readonly TimeSpan BlockRetryDelay = TimeSpan.FromSeconds(3);

    public string Key => "news";
    public int Order => 10;
    public SegmentLabel Label => new(AnnouncementKind.News, "NewsPackage", "News update");

    public bool IsEnabled(StationSettings settings) => settings.NewsEnabled;

    public int CadenceMinutes(StationSettings settings)
        => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes);

    public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
        => settings.NewsEnabled && IsCadenceBoundary(targetLocal, CadenceMinutes(settings));

    public async Task<SegmentResult> ProduceAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var factory = context.ScopeServices.GetRequiredService<AnnouncementFactory>();
        var extractor = context.ScopeServices.GetRequiredService<INewsArticleExtractor>();
        var specialistHosts = context.ScopeServices.GetRequiredService<SpecialistHostCreationService>();

        await context.ReportProgress("Resolving news specialist.", ct);
        var newsModerator = await ResolveNewsModeratorAsync(settings, specialistHosts, ct);

        // Fetch + select news items.
        List<NewsItem> items = [];
        await context.ReportProgress("Fetching news.", ct);
        await feedPolling.PollEnabledFeedsAsync(ct);
        await context.ReportProgress("Selecting news items.", ct);
        await using (var itemDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var candidates = await itemDb.NewsItems
                .Include(item => item.Feed)
                .Where(item => item.Status == NewsItemStatus.New
                    && item.Feed != null
                    && item.Feed.IsEnabled)
                .ToListAsync(ct);
            // Surface the freshest candidates, then arrange that pool in the station's
            // news running order so the bulletin reads top-priority category first.
            var freshest = candidates
                .OrderByDescending(item => item.PublishedAtUtc)
                .Take(MaxCandidateItems);
            items = NewsCategoryOrdering
                .SortItems(freshest, NewsCategoryOrdering.Parse(settings.NewsCategoryOrder))
                .ToList();
            foreach (var item in items)
            {
                item.Status = NewsItemStatus.Selected;
                item.SelectionReason = "Top-of-hour package";
            }
            await itemDb.SaveChangesAsync(ct);
        }

        // Always produce the intro handover (LLM with direct-text fallback).
        var intro = await ProduceIntroAsync(context, factory, newsModerator, items, ct);

        // Produce the news bulletin body (null when no items or after retries exhausted).
        Announcement? body = null;
        string? degradationReason = null;
        if (items.Count > 0)
        {
            body = await TryProduceBodyAsync(context, factory, extractor, newsModerator, items, ct);
            if (body is null)
            {
                degradationReason = "News bulletin failed after retries; airing handover only.";
            }
        }
        else
        {
            degradationReason = "No news items available for this bulletin.";
        }

        // Gap line when there is no body.
        Announcement? gapLine = null;
        if (body is null)
        {
            await context.ReportProgress("Recording news gap line.", ct);
            gapLine = await factory.ProduceDirectAsync(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                "We'll have news for you later this hour.",
                "NewsGap",
                ct,
                title: "News gap",
                expiresAtUtc: context.ExpiresAtUtc,
                desiredDurationSeconds: 5,
                wordBudget: 12);
        }

        var sourceSummary = items.Count > 0
            ? string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"))
            : "News update (no items available)";

        return new SegmentResult(
            newsModerator,
            intro,
            body,
            gapLine,
            items,
            degradationReason,
            sourceSummary);
    }

    private async Task<Announcement> ProduceIntroAsync(
        SegmentProductionContext context,
        AnnouncementFactory factory,
        Moderator newsModerator,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        var timeText = context.TargetLocal.ToString("HH:mm");
        var positionNote = context.Position == SegmentPosition.First
            ? "This is the first segment of the top-of-hour block."
            : $"This segment follows {context.PreviousSegmentHost?.Name ?? "the previous segment"}.";
        var tease = BuildTease(items.Count > 0, items);
        var facts = $"Current time: {timeText}. Current host: {context.ShowModerator.Name}. News specialist: {newsModerator.Name}. {positionNote} {tease}";

        await context.ReportProgress("Writing news handover.", ct);
        try
        {
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                context.ShowModerator,
                relatedTrack: null,
                facts: facts,
                context.Settings.StationName,
                ct,
                lengthHint: "1-2 sentences, warm and natural.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "NewsHandover");
            return await factory.ProduceFromDraftAsync(draft, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "News handover LLM/TTS failed; falling back to direct text");
            var fallback = BuildIntroText(context.ShowModerator, newsModerator, context.TargetLocal);
            return await factory.ProduceDirectAsync(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                fallback,
                "NewsHandover",
                ct,
                title: "Top of hour",
                expiresAtUtc: context.ExpiresAtUtc,
                desiredDurationSeconds: 8,
                wordBudget: 22);
        }
    }

    private static string BuildTease(bool hasItems, IReadOnlyList<NewsItem> newsItems)
    {
        if (newsItems.Count == 0)
        {
            return "There are no fresh news items this hour; keep the handover brief and do not tease headlines.";
        }

        var titles = newsItems.Take(3).Select(item => item.Title);
        return $"Tease the news briefly. Upcoming stories include: {string.Join("; ", titles)}.";
    }

    private async Task<Announcement?> TryProduceBodyAsync(
        SegmentProductionContext context,
        AnnouncementFactory factory,
        INewsArticleExtractor extractor,
        Moderator newsModerator,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= BlockMaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    logger.LogInformation("Retrying news bulletin ({Attempt}/{Max})", attempt, BlockMaxAttempts);
                }

                await context.ReportProgress("Extracting article text.", ct);
                await EnrichItemsAsync(items, context.Settings.NewsExtractionEnabled, extractor, ct);
                await context.ReportProgress("Writing news script.", ct);
                var handoff = BuildIntroText(context.ShowModerator, newsModerator, context.TargetLocal);
                var draft = await factory.WriteScriptDraftAsync(
                    AnnouncementKind.News,
                    newsModerator,
                    relatedTrack: null,
                    facts: BuildNewsFacts(items, context.TargetLocal),
                    context.Settings.StationName,
                    ct,
                    lengthHint: $"A full top-of-hour news bulletin of up to {Math.Max(1, context.Settings.NewsPackageMaxDurationSeconds / 60)} minutes. Write one short paragraph per story, lead with the strongest, and drop weak or duplicate stories rather than padding.",
                    alreadySpokenContext: handoff,
                    localNowOverride: context.TargetLocal,
                    priority: PromptPriority.High,
                    purpose: "NewsReport");
                await context.ReportProgress("Recording news bulletin.", ct);
                return await factory.ProduceFromDraftAsync(draft, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                metrics.GenerationFailed("news");
                logger.LogWarning(
                    ex,
                    "News bulletin failed on attempt {Attempt}/{Max}: {Message}",
                    attempt,
                    BlockMaxAttempts,
                    ex.GetBaseException().Message);
                if (attempt < BlockMaxAttempts)
                {
                    await Task.Delay(BlockRetryDelay, ct);
                }
            }
        }

        logger.LogError(
            "News bulletin skipped after {MaxAttempts} failed attempts: {Message}",
            BlockMaxAttempts,
            last?.GetBaseException().Message);
        return null;
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

    private static bool IsCadenceBoundary(DateTimeOffset localTime, int cadenceMinutes)
    {
        var minuteOfDay = localTime.Hour * 60 + localTime.Minute;
        return minuteOfDay % cadenceMinutes == 0;
    }
}
