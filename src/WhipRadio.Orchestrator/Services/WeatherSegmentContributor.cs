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

    public int CadenceMinutes(StationSettings settings)
        => WeatherScheduler.NormalizeCadence(settings.WeatherCadenceMinutes);

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

        var handoverFacts = BuildHandoverFacts(context, weatherModerator);
        var jobs = new List<SegmentDraftJob>
        {
            new(SegmentSlot.Handover, 0, "weather handoff",
                (sp, token) => WriteHandoverAsync(sp, context, weatherModerator, handoverFacts, token)),
            new(SegmentSlot.Body, 1, "weather forecast",
                (sp, token) => WriteBodyAsync(sp, context, weatherModerator, token)),
        };

        return new SegmentDraftPlan(Key, weatherModerator, [], "Weather forecast", jobs);
    }

    private async Task<SlotDraft> WriteHandoverAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator weatherModerator,
        string facts,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress("Writing weather handoff.", ct);
        try
        {
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                context.ShowModerator,
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
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.WeatherHandoff,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                $"{weatherModerator.Name} has the weather.",
                "WeatherHandoff",
                "Weather handoff",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 5,
                WordBudget: 14);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private static string BuildHandoverFacts(SegmentProductionContext context, Moderator weatherModerator)
    {
        var positionNote = context.Position == SegmentPosition.First
            ? "This is the first segment of the block."
            : $"This segment follows {context.PreviousSegmentHost?.Name ?? "the previous segment"}.";
        return $"Current host: {context.ShowModerator.Name}. Weather specialist: {weatherModerator.Name}. {positionNote}";
    }

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
                await context.ReportProgress("Writing weather script.", ct);
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
