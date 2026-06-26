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

    /// <summary>How many candidate stories per topic we hand the bulletin writer. We pass more
    /// than will air per topic: the writer curates — leading with the strongest, merging
    /// duplicates, and dropping weak items — so it needs a real pool to pick from.</summary>
    private const int MaxCandidatesPerCategory = 4;

    /// <summary>Overall cap on the candidate pool handed to the writer across all topics.</summary>
    private const int MaxCandidateItems = 24;

    private static readonly TimeSpan BlockRetryDelay = TimeSpan.FromSeconds(3);

    public string Key => "news";
    public int Order => 10;
    public SegmentLabel Label => new(AnnouncementKind.News, "NewsPackage", "News update");

    public bool IsEnabled(StationSettings settings) => settings.NewsEnabled;

    public int CadenceMinutes(StationSettings settings)
        => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes);

    public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
        => settings.NewsEnabled && IsCadenceBoundary(targetLocal, CadenceMinutes(settings));

    public async Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var specialistHosts = context.ScopeServices.GetRequiredService<SpecialistHostCreationService>();

        await context.ReportProgress("Resolving news specialist.", ct);
        var newsModerator = await ResolveNewsModeratorAsync(settings, specialistHosts, ct);

        // Fetch + select news items (cheap prep — no GPU work).
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
            // Build a topic-balanced candidate pool: freshest few per category, in the
            // station's news running order, so the writer can cover several topics with at
            // least two stories each rather than a single topic dominating the bulletin.
            items = SelectBalancedCandidates(
                candidates, NewsCategoryOrdering.Parse(settings.NewsCategoryOrder)).ToList();
            foreach (var item in items)
            {
                item.Status = NewsItemStatus.Selected;
                item.SelectionReason = "Top-of-hour package";
            }
            await itemDb.SaveChangesAsync(ct);
        }

        var sourceSummary = items.Count > 0
            ? string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"))
            : "News update (no items available)";
        var handoverFacts = BuildHandoverFacts(context, newsModerator, items);

        var jobs = new List<SegmentDraftJob>
        {
            new(SegmentSlot.Handover, 0, ScriptOperationLabels.Describe(AnnouncementKind.StationId, "NewsHandover"),
                (sp, token) => WriteHandoverAsync(sp, context, newsModerator, handoverFacts, token)),
            new(SegmentSlot.Body, 1, ScriptOperationLabels.Describe(AnnouncementKind.News, "NewsReport"),
                (sp, token) => WriteBodyAsync(sp, context, newsModerator, items, token)),
        };

        return new SegmentDraftPlan(Key, newsModerator, items, sourceSummary, jobs);
    }

    private async Task<SlotDraft> WriteHandoverAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator newsModerator,
        string facts,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.StationId, "NewsHandover")}.", ct);
        try
        {
            // The news host opens the block by briefly introducing THEMSELVES (not the show
            // host introducing them), so the handover is voiced by the news anchor.
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                newsModerator,
                relatedTrack: null,
                facts: facts,
                context.Settings.StationName,
                ct,
                lengthHint: "1-2 sentences, warm and natural.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "NewsHandover");
            return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "News intro LLM failed; falling back to direct text");
            var fallback = BuildSelfIntroText(newsModerator, context.TargetLocal);
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                newsModerator,
                fallback,
                "NewsHandover",
                "Top of hour",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 8,
                WordBudget: 22);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private static string BuildHandoverFacts(
        SegmentProductionContext context, Moderator newsModerator, IReadOnlyList<NewsItem> items)
    {
        var timeText = context.TargetLocal.ToString("HH:mm");
        var teaseNote = items.Count > 0
            ? "Then go straight into the news; do not read any headlines in this intro."
            : "There are no fresh news items this hour; keep the intro brief.";
        return $"News anchor (speaking): {newsModerator.Name}. Current time: {timeText}. "
            + $"This opens the top-of-hour news. {teaseNote}";
    }

    private static SlotDraft NewsGapSlot(SegmentProductionContext context, string reason)
        => new(
            null,
            new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                "We'll have news for you later this hour.",
                "NewsGap",
                "News gap",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 5,
                WordBudget: 12),
            IsGap: true,
            DegradationReason: reason);

    /// <summary>
    /// Picks a topic-balanced candidate pool: the freshest few stories per category (so each
    /// topic the writer covers can carry at least two stories), flattened in the station's
    /// news running order, then capped overall. The writer curates this pool down to air.
    /// </summary>
    internal static IReadOnlyList<NewsItem> SelectBalancedCandidates(
        IEnumerable<NewsItem> candidates, IReadOnlyList<string> categoryOrder)
    {
        var perCategory = candidates
            .GroupBy(item => NormalizeCategory(item.Feed?.Category))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.PublishedAtUtc)
                    .Take(MaxCandidatesPerCategory)
                    .ToList());

        // Emit categories in the station's priority order first, then any remaining categories.
        var orderedKeys = categoryOrder
            .Select(NormalizeCategory)
            .Concat(perCategory.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var selected = new List<NewsItem>();
        foreach (var key in orderedKeys)
        {
            if (perCategory.TryGetValue(key, out var group))
            {
                selected.AddRange(group);
            }
        }

        return selected.Take(MaxCandidateItems).ToList();
    }

    private static string NormalizeCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();

    private async Task<SlotDraft> WriteBodyAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator newsModerator,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return NewsGapSlot(context, "No news items available for this bulletin.");
        }

        var factory = sp.GetRequiredService<AnnouncementFactory>();
        var extractor = sp.GetRequiredService<INewsArticleExtractor>();
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
                await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.News, "NewsReport")}.", ct);
                var handoff = BuildSelfIntroText(newsModerator, context.TargetLocal);
                var draft = await factory.WriteScriptDraftAsync(
                    AnnouncementKind.News,
                    newsModerator,
                    relatedTrack: null,
                    facts: BuildNewsFacts(items, context.TargetLocal),
                    context.Settings.StationName,
                    ct,
                    lengthHint: "A full top-of-hour news bulletin of about three to five minutes. Cover eight to ten stories across the topics, at least two per topic where available, one short paragraph per story, topic by topic with no spoken topic transitions. Lead with the strongest and drop weak or duplicate stories rather than padding.",
                    alreadySpokenContext: handoff,
                    localNowOverride: context.TargetLocal,
                    priority: PromptPriority.High,
                    purpose: "NewsReport");
                return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
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
        return NewsGapSlot(context, "News bulletin failed after retries; airing handover only.");
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

    /// <summary>The news host's short self-introduction — spoken at the top of the block and
    /// passed to the bulletin writer as "already said" context so it doesn't repeat it.</summary>
    internal static string BuildSelfIntroText(Moderator newsModerator, DateTimeOffset localNow)
    {
        var timeText = localNow.ToString("HH:mm");
        return $"It's {timeText}. I'm {newsModerator.Name} with your news.";
    }

    /// <summary>
    /// Renders the candidate stories grouped under topic headings (in the station's news
    /// running order) so the writer sees the topic structure directly and can cover each
    /// topic with at least two stories.
    /// </summary>
    internal static string BuildNewsFacts(IEnumerable<NewsItem> items, DateTimeOffset localNow)
    {
        var grouped = items
            .GroupBy(item => NormalizeCategory(item.Feed?.Category))
            .ToList();

        var blocks = grouped.Select(group =>
        {
            var heading = $"== TOPIC: {group.Key} ==";
            var stories = string.Join(
                "\n\n",
                group.Select((item, index) =>
                    $"{index + 1}. Source: {item.Feed?.Label ?? "Unknown"}\n"
                    + $"Title: {item.Title}\n"
                    + $"Published UTC: {item.PublishedAtUtc:O}\n"
                    + $"Summary/source text: {FirstNonEmpty(item.ExtractedSummary, item.Summary, "No summary available.")}\n"
                    + $"URL: {item.Url}"));
            return $"{heading}\n{stories}";
        });

        return $"Bulletin time: {localNow:yyyy-MM-dd HH:mm} local. Stories are grouped by topic below.\n\n"
            + string.Join("\n\n", blocks);
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsCadenceBoundary(DateTimeOffset localTime, int cadenceMinutes)
    {
        var minuteOfDay = localTime.Hour * 60 + localTime.Minute;
        return minuteOfDay % cadenceMinutes == 0;
    }
}
