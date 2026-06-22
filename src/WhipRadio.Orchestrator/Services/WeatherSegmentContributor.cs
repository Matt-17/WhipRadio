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

    public async Task<SegmentResult> ProduceAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var factory = context.ScopeServices.GetRequiredService<AnnouncementFactory>();
        var weatherSource = context.ScopeServices.GetRequiredService<IWeatherReportSource>();
        var specialistHosts = context.ScopeServices.GetRequiredService<SpecialistHostCreationService>();

        await context.ReportProgress("Resolving weather specialist.", ct);
        var weatherModerator = await ResolveWeatherModeratorAsync(
            settings,
            context.PreviousSegmentHost,
            specialistHosts,
            ct);

        // Always produce the weather handoff (LLM with direct-text fallback).
        var intro = await ProduceIntroAsync(context, factory, weatherModerator, ct);

        // Produce the weather forecast body (null after retries exhausted).
        var body = await TryProduceBodyAsync(context, factory, weatherSource, weatherModerator, ct);

        Announcement? gapLine = null;
        string? degradationReason = null;
        if (body is null)
        {
            degradationReason = "Weather forecast failed after retries; airing handoff only.";
            await context.ReportProgress("Recording weather gap line.", ct);
            gapLine = await factory.ProduceDirectAsync(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                "We'll have the weather for you in a moment.",
                "WeatherGap",
                ct,
                title: "Weather gap",
                expiresAtUtc: context.ExpiresAtUtc,
                desiredDurationSeconds: 5,
                wordBudget: 12);
        }

        return new SegmentResult(
            weatherModerator,
            intro,
            body,
            gapLine,
            [],
            degradationReason,
            "Weather forecast");
    }

    private async Task<Announcement> ProduceIntroAsync(
        SegmentProductionContext context,
        AnnouncementFactory factory,
        Moderator weatherModerator,
        CancellationToken ct)
    {
        var positionNote = context.Position == SegmentPosition.First
            ? "This is the first segment of the block."
            : $"This segment follows {context.PreviousSegmentHost?.Name ?? "the previous segment"}.";
        var facts = $"Current host: {context.ShowModerator.Name}. Weather specialist: {weatherModerator.Name}. {positionNote}";

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
            return await factory.ProduceFromDraftAsync(draft, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Weather handoff LLM/TTS failed; falling back to direct text");
            var fallback = $"{weatherModerator.Name} has the weather.";
            return await factory.ProduceDirectAsync(
                AnnouncementKind.StationId,
                TalkPartKind.WeatherHandoff,
                TalkBreakPriority.Scheduled,
                context.ShowModerator,
                fallback,
                "WeatherHandoff",
                ct,
                title: "Weather handoff",
                expiresAtUtc: context.ExpiresAtUtc,
                desiredDurationSeconds: 5,
                wordBudget: 14);
        }
    }

    private async Task<Announcement?> TryProduceBodyAsync(
        SegmentProductionContext context,
        AnnouncementFactory factory,
        IWeatherReportSource weatherSource,
        Moderator weatherModerator,
        CancellationToken ct)
    {
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
                await context.ReportProgress("Recording weather forecast.", ct);
                return await factory.ProduceFromDraftAsync(draft, ct);
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
        return null;
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
