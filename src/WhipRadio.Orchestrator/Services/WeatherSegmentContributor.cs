using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Weather segment contributor for top-of-hour packages. Produces an
/// LLM-written weather handoff (with direct-text fallback) and a weather
/// forecast. The handoff always airs; the forecast may fail after retries,
/// in which case a gap line + degradation reason are emitted.
/// </summary>
public sealed class WeatherSegmentContributor(
    IDbContextFactory<RadioDbContext> dbFactory,
    IStationMetrics metrics,
    ILogger<WeatherSegmentContributor> logger) : ITopOfHourSegmentContributor
{
    private const int BlockMaxAttempts = 3;
    private static readonly TimeSpan BlockRetryDelay = TimeSpan.FromSeconds(3);

    public string Key => "weather";
    public int Order => 20;
    public SegmentLabel Label => new(AnnouncementKind.Weather, "WeatherReport", "Weather");

    public bool IsEnabled(StationSettings settings) => settings.WeatherEnabled;

    // Weather now rides the single top-of-hour cadence so it always airs inside the news
    // block (when news is on) rather than on its own separate schedule.
    public int CadenceMinutes(StationSettings settings)
        => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes);

    public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
        => settings.WeatherEnabled && IsCadenceBoundary(targetLocal, CadenceMinutes(settings));

    public async Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var specialistHosts = context.ScopeServices.GetRequiredService<SpecialistHostCreationService>();

        await context.ReportProgress("Resolving weather specialist.", ct);
        var weatherModerator = await ResolveWeatherModeratorAsync(
            settings,
            context.PreviousSegmentHost,
            specialistHosts,
            ct);

        // The news host hands over to the weather (and returns afterwards). When weather leads
        // the block (news off), the weather host introduces themselves and there is no return.
        var newsHost = context.PriorHosts.LastOrDefault();
        var handoverFacts = BuildHandoverFacts(context, weatherModerator, newsHost);
        var handoverHost = newsHost ?? weatherModerator;
        var jobs = new List<SegmentDraftJob>
        {
            new(SegmentSlot.Handover, 0, ScriptOperationLabels.Describe(AnnouncementKind.StationId, "WeatherHandoff"),
                (sp, token) => WriteHandoverAsync(sp, context, handoverHost, weatherModerator, newsHost is null, handoverFacts, token)),
            new(SegmentSlot.Body, 1, ScriptOperationLabels.Describe(AnnouncementKind.Weather, "WeatherReport"),
                (sp, token) => WriteBodyAsync(sp, context, weatherModerator, token)),
        };

        if (newsHost is not null)
        {
            var returnFacts = BuildReturnFacts(context, weatherModerator, newsHost);
            jobs.Add(new(SegmentSlot.Outro, 2, ScriptOperationLabels.Describe(AnnouncementKind.StationId, "WeatherReturn"),
                (sp, token) => WriteReturnAsync(sp, context, newsHost, weatherModerator, returnFacts, token)));
        }

        return new SegmentDraftPlan(Key, weatherModerator, [], "Weather forecast", jobs);
    }

    private async Task<SlotDraft> WriteHandoverAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator handoverHost,
        Moderator weatherModerator,
        bool isSelfIntro,
        string facts,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.StationId, "WeatherHandoff")}.", ct);
        try
        {
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                handoverHost,
                relatedTrack: null,
                facts: facts,
                context.Settings.StationName,
                ct,
                lengthHint: "One natural sentence.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "WeatherHandoff");
            return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Weather handoff LLM failed; falling back to direct text");
            var fallback = isSelfIntro
                ? $"I'm {weatherModerator.Name} with your weather."
                : $"Now {weatherModerator.Name} has the weather.";
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.WeatherHandoff,
                TalkBreakPriority.Scheduled,
                handoverHost,
                fallback,
                "WeatherHandoff",
                "Weather handoff",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 5,
                WordBudget: 14);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private async Task<SlotDraft> WriteReturnAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator newsHost,
        Moderator weatherModerator,
        string facts,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.StationId, "WeatherReturn")}.", ct);
        try
        {
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                newsHost,
                relatedTrack: null,
                facts: facts,
                context.Settings.StationName,
                ct,
                lengthHint: "One short, natural sentence.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "WeatherReturn");
            return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Weather return LLM failed; falling back to direct text");
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                newsHost,
                $"Thanks, {weatherModerator.Name}. And that's your news and weather. Back to you, {context.ShowModerator.Name}.",
                "WeatherReturn",
                "Weather return",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 6,
                WordBudget: 18);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private static string BuildHandoverFacts(
        SegmentProductionContext context, Moderator weatherModerator, Moderator? newsHost)
        => newsHost is null
            ? $"Weather specialist (speaking, self-introducing): {weatherModerator.Name}. "
                + "Weather leads this block; introduce yourself briefly before the forecast."
            : $"News host (speaking): {newsHost.Name}. Weather specialist: {weatherModerator.Name}. "
                + "The news bulletin just finished; hand over to the weather specialist.";

    private static string BuildReturnFacts(
        SegmentProductionContext context, Moderator weatherModerator, Moderator newsHost)
        => $"News host (speaking): {newsHost.Name}. Weather specialist who just finished: {weatherModerator.Name}. "
            + $"Show host to hand back to: {context.ShowModerator.Name}. "
            + "Wrap the news and weather and hand back to the show host.";

    private static SlotDraft WeatherGapSlot(SegmentProductionContext context, string reason)
        => new(
            null,
            new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                "We'll have the weather for you in a moment.",
                "WeatherGap",
                "Weather gap",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 5,
                WordBudget: 12),
            IsGap: true,
            DegradationReason: reason);

    private async Task<SlotDraft> WriteBodyAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator weatherModerator,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        var weatherSource = sp.GetRequiredService<IWeatherReportSource>();
        Exception? last = null;
        for (var attempt = 1; attempt <= BlockMaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    logger.LogInformation("Retrying weather forecast ({Attempt}/{Max})", attempt, BlockMaxAttempts);
                }

                var handoff = $"{weatherModerator.Name} has the weather.";
                await context.ReportProgress("Loading weather report.", ct);
                var report = await weatherSource.GetReportAsync(weatherModerator.Language, ct);
                await context.ReportProgress($"{ScriptOperationLabels.Writing(AnnouncementKind.Weather, "WeatherReport")}.", ct);
                var draft = await factory.WriteScriptDraftAsync(
                    AnnouncementKind.Weather,
                    weatherModerator,
                    relatedTrack: null,
                    facts: report.ToFacts(context.TargetLocal.DateTime),
                    context.Settings.StationName,
                    ct,
                    lengthHint: "A concise weather report, about 45 seconds.",
                    alreadySpokenContext: handoff,
                    localNowOverride: context.TargetLocal,
                    priority: PromptPriority.High,
                    purpose: "WeatherReport");
                return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                metrics.GenerationFailed("weather");
                logger.LogWarning(
                    ex,
                    "Weather forecast failed on attempt {Attempt}/{Max}: {Message}",
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
            "Weather forecast skipped after {MaxAttempts} failed attempts: {Message}",
            BlockMaxAttempts,
            last?.GetBaseException().Message);
        return WeatherGapSlot(context, "Weather forecast failed after retries; airing handoff only.");
    }

    private async Task<Moderator> ResolveWeatherModeratorAsync(
        StationSettings settings,
        Moderator? previousSegmentHost,
        SpecialistHostCreationService specialistHosts,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        var resolved = ProductionSpecialistPolicy.ResolveWeatherModerator(
            settings,
            moderators,
            previousSegmentHost ?? new Moderator { Id = int.MinValue });
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

    private static bool IsCadenceBoundary(DateTimeOffset localTime, int cadenceMinutes)
    {
        var minuteOfDay = localTime.Hour * 60 + localTime.Minute;
        return minuteOfDay % cadenceMinutes == 0;
    }
}
