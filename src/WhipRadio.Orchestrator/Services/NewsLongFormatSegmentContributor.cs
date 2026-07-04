using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The scheduled long news show (Phase 3c.1 §3): a chaptered block of topic
/// bulletins read by the news anchor at operator-configured air times, produced
/// through the same package pipeline as the short top-of-hour bulletin. Each
/// chapter is its own write+voice job so a single failed topic degrades to a
/// shorter show instead of killing the block.
/// </summary>
public sealed class NewsLongFormatSegmentContributor(
    IDbContextFactory<RadioDbContext> dbFactory,
    NewsFeedPollingService feedPolling,
    IStationMetrics metrics,
    ILogger<NewsLongFormatSegmentContributor> logger) : ITopOfHourSegmentContributor
{
    public const string SegmentKey = "news-long";

    private const int ChapterMaxAttempts = 3;
    private const int MaxCandidatesPerCategory = 8;
    private const int MaxCandidateItems = 48;

    /// <summary>Minutes reserved for the handover, weather, and show return around the chapters.</summary>
    private const int OverheadMinutes = 3;
    private const int TargetChapterMinutes = 4;
    private const int MinChapters = 2;
    private const int MaxChapters = 7;

    private static readonly TimeSpan ChapterRetryDelay = TimeSpan.FromSeconds(3);

    public string Key => SegmentKey;
    public int Order => 10;
    public SegmentLabel Label => new(AnnouncementKind.News, "LongFormatNews", "News block");

    public bool IsEnabled(StationSettings settings)
        => settings.NewsEnabled && settings.NewsLongFormatEnabled;

    public int CadenceMinutes(StationSettings settings)
        => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes);

    public DateTimeOffset? NextOwnTarget(StationSettings settings, DateTimeOffset localNow)
        => LongFormatNewsScheduler.NextTarget(
            localNow, LongFormatNewsScheduler.ParseAirTimes(settings.NewsLongFormatAirTimes));

    public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
        => IsEnabled(settings)
            && LongFormatNewsScheduler.IsAirTime(
                targetLocal, LongFormatNewsScheduler.ParseAirTimes(settings.NewsLongFormatAirTimes));

    public async Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var specialistHosts = context.ScopeServices.GetRequiredService<SpecialistHostCreationService>();

        await context.ReportProgress("Resolving news specialist.", ct);
        var newsModerator = await NewsSegmentContributor.ResolveNewsModeratorAsync(
            dbFactory, settings, specialistHosts, logger, ct);

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
            items = NewsSegmentContributor.SelectBalancedCandidates(
                    candidates,
                    NewsCategoryOrdering.Parse(settings.NewsCategoryOrder),
                    MaxCandidatesPerCategory,
                    MaxCandidateItems)
                .ToList();
            foreach (var item in items)
            {
                item.Status = NewsItemStatus.Selected;
                item.SelectionReason = "Long news format";
            }
            await itemDb.SaveChangesAsync(ct);
        }

        var sourceSummary = items.Count > 0
            ? string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"))
            : "News block (no items available)";

        var jobs = new List<SegmentDraftJob>
        {
            new(SegmentSlot.Handover, 0, ScriptOperationLabels.Describe(AnnouncementKind.StationId, "NewsHandover"),
                (sp, token) => WriteHandoverAsync(sp, context, newsModerator, items, token)),
        };

        if (items.Count == 0)
        {
            jobs.Add(new SegmentDraftJob(
                SegmentSlot.Body, 1, ScriptOperationLabels.Describe(AnnouncementKind.News, "NewsChapter"),
                (_, _) => Task.FromResult(GapSlot(context, "No news items available for the news block."))));
            return new SegmentDraftPlan(Key, newsModerator, items, sourceSummary, jobs);
        }

        var chapters = BuildChapters(
            items,
            NewsCategoryOrdering.Parse(settings.NewsCategoryOrder),
            LongFormatNewsScheduler.NormalizeDurationMinutes(settings.NewsLongFormatDurationMinutes));
        var chapterMinutes = ChapterMinutes(settings, chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var chapterIndex = i + 1;
            jobs.Add(new SegmentDraftJob(
                SegmentSlot.Body,
                chapterIndex,
                $"{ScriptOperationLabels.Describe(AnnouncementKind.News, "NewsChapter")} {chapterIndex}/{chapters.Count} ({chapter.Category})",
                (sp, token) => WriteChapterAsync(
                    sp, context, newsModerator, chapter, chapterIndex, chapters.Count, chapterMinutes, token)));
        }

        return new SegmentDraftPlan(Key, newsModerator, items, sourceSummary, jobs);
    }

    internal sealed record Chapter(string Category, IReadOnlyList<NewsItem> Items);

    /// <summary>
    /// Groups the candidate pool into topic chapters in the station's running order,
    /// capped by the show length: roughly one chapter per ~4 minutes after overhead,
    /// between 2 and 7. Surplus topics fold into the last chapter so no story is lost.
    /// </summary>
    internal static IReadOnlyList<Chapter> BuildChapters(
        IReadOnlyList<NewsItem> items, IReadOnlyList<string> categoryOrder, int durationMinutes)
    {
        var budget = ChapterBudget(durationMinutes);
        var grouped = items
            .GroupBy(item => NewsSegmentContributor.NormalizeCategory(item.Feed?.Category))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var orderedCategories = categoryOrder
            .Select(NewsSegmentContributor.NormalizeCategory)
            .Concat(grouped.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(grouped.ContainsKey)
            .ToList();

        var chapters = new List<Chapter>();
        foreach (var category in orderedCategories)
        {
            if (chapters.Count < budget)
            {
                chapters.Add(new Chapter(category, grouped[category]));
            }
            else
            {
                var last = chapters[^1];
                chapters[^1] = new Chapter(last.Category, [.. last.Items, .. grouped[category]]);
            }
        }

        return chapters;
    }

    internal static int ChapterBudget(int durationMinutes)
        => Math.Clamp(
            (LongFormatNewsScheduler.NormalizeDurationMinutes(durationMinutes) - OverheadMinutes) / TargetChapterMinutes,
            MinChapters,
            MaxChapters);

    private static int ChapterMinutes(StationSettings settings, int chapterCount)
    {
        var duration = LongFormatNewsScheduler.NormalizeDurationMinutes(settings.NewsLongFormatDurationMinutes);
        return Math.Max(2, (duration - OverheadMinutes) / Math.Max(1, chapterCount));
    }

    private async Task<SlotDraft> WriteHandoverAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator newsModerator,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.StationId, "NewsHandover")}.", ct);
        try
        {
            var timeText = context.TargetLocal.ToString("HH:mm");
            var teaseNote = items.Count > 0
                ? "Welcome the listeners to the full news show and go straight into the first topic; do not read any headlines in this intro."
                : "There are no fresh news items; keep the welcome brief.";
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                newsModerator,
                relatedTrack: null,
                facts: $"News anchor (speaking): {newsModerator.Name}. Current time: {timeText}. "
                    + $"This opens the station's long news show. {teaseNote}",
                context.Settings.StationName,
                ct,
                lengthHint: "2-3 sentences, welcoming and confident.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "NewsHandover");
            return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "News show intro LLM failed; falling back to direct text");
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                newsModerator,
                NewsSegmentContributor.BuildSelfIntroText(newsModerator, context.TargetLocal),
                "NewsHandover",
                "News block",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 8,
                WordBudget: 22);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private async Task<SlotDraft> WriteChapterAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator newsModerator,
        Chapter chapter,
        int chapterIndex,
        int chapterCount,
        int chapterMinutes,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        var closingNote = chapterIndex == chapterCount
            ? "This is the final topic: close the whole news block with one short sign-off line."
            : "More topics follow: do NOT sign off or conclude the show; end on the last story.";
        var facts = $"Chapter {chapterIndex} of {chapterCount} of the long news show. Topic: {chapter.Category}. "
            + $"{closingNote}\n\n"
            + NewsSegmentContributor.BuildNewsFacts(chapter.Items, context.TargetLocal);

        Exception? last = null;
        for (var attempt = 1; attempt <= ChapterMaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    logger.LogInformation(
                        "Retrying news chapter {Chapter}/{Total} ({Attempt}/{Max})",
                        chapterIndex, chapterCount, attempt, ChapterMaxAttempts);
                }

                await context.ReportProgress(
                    $"{ScriptOperationLabels.Writing(AnnouncementKind.News, "NewsChapter")} {chapterIndex}/{chapterCount}.", ct);
                var draft = await factory.WriteScriptDraftAsync(
                    AnnouncementKind.News,
                    newsModerator,
                    relatedTrack: null,
                    facts: facts,
                    context.Settings.StationName,
                    ct,
                    lengthHint: $"One news show chapter of about {chapterMinutes} minutes on a single topic. "
                        + "Cover the strongest stories in depth, one or two paragraphs per story; merge duplicates "
                        + "and drop weak items rather than padding. No greeting — the show is already running.",
                    alreadySpokenContext: NewsSegmentContributor.BuildSelfIntroText(newsModerator, context.TargetLocal),
                    localNowOverride: context.TargetLocal,
                    priority: PromptPriority.High,
                    purpose: "NewsChapter");
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
                    "News chapter {Chapter}/{Total} failed on attempt {Attempt}/{Max}: {Message}",
                    chapterIndex, chapterCount, attempt, ChapterMaxAttempts, ex.GetBaseException().Message);
                if (attempt < ChapterMaxAttempts)
                {
                    await Task.Delay(ChapterRetryDelay, ct);
                }
            }
        }

        // The chapter is dropped (no gap filler mid-show — the block just runs shorter).
        throw new InvalidOperationException(
            $"News chapter {chapterIndex}/{chapterCount} ({chapter.Category}) failed after "
            + $"{ChapterMaxAttempts} attempts.", last);
    }

    private static SlotDraft GapSlot(SegmentProductionContext context, string reason)
        => new(
            null,
            new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                "We'll be back with the full news show later today.",
                "NewsGap",
                "News gap",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 5,
                WordBudget: 14),
            IsGap: true,
            DegradationReason: reason);
}
