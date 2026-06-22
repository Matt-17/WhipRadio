using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Talks are produced fresh by the ShowRunner for the gap they air in — there is
/// no generic talk pool anymore. This service only prepares the two things that
/// need lead time: queued listener greetings (read ASAP) and the hourly weather
/// report (prepared in the last minutes of the hour, aired right after the top).
/// </summary>
public class AnnouncementProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    TimeProvider timeProvider,
    IStationMetrics metrics,
    ILogger<AnnouncementProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);

    /// <summary>After this, an unfulfilled request goes to the mailbag with an honest "not available".</summary>
    private const int RequestFulfillmentTimeoutMinutes = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            const string kind = "announcement";
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
                logger.LogError(ex,
                    "Announcement production cycle failed ({Reason}); retrying in {Delay}s",
                    ex.GetBaseException().Message, CycleDelay.TotalSeconds);
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
        var weatherSource = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();

        var context = await schedule.GetCurrentAsync(ct);
        var moderator = context.Moderator;

        StationSettings settings;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        }

        // Queued listener greetings jump the line — listeners are waiting.
        if (await TryProduceGreetingAsync(factory, context, settings.StationName, ct))
        {
            return;
        }

        // Weather is hourly, on the full hour: prepare a FRESH report in the last
        // minutes of the hour so it's ready to air right after the top.
        var localNow = timeProvider.GetLocalNow();
        if (settings.WeatherEnabled
            && !settings.NewsEnabled
            && WeatherScheduler.ShouldPrepare(localNow, settings.WeatherCadenceMinutes)
            && !await HasFreshUnplayedWeatherAsync(settings.WeatherCadenceMinutes, ct))
        {
            var weatherModerator = await ResolveWeatherModeratorAsync(settings, moderator, ct);
            var airingLocalTime = WeatherScheduler.NextWindowStart(localNow, settings.WeatherCadenceMinutes);
            var report = await weatherSource.GetReportAsync(weatherModerator.Language, ct);
            await factory.ProduceAsync(
                AnnouncementKind.Weather,
                weatherModerator,
                null,
                report.ToFacts(airingLocalTime.DateTime),
                settings.StationName,
                ct,
                localNowOverride: airingLocalTime);
        }
    }

    private async Task<bool> HasFreshUnplayedWeatherAsync(int cadenceMinutes, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var freshCutoff = timeProvider.GetUtcNow().UtcDateTime
            .AddMinutes(-WeatherScheduler.NormalizeCadence(cadenceMinutes));
        var weatherAnnouncementIds = await db.TalkParts.AsNoTracking()
            .Where(part => part.Kind == TalkPartKind.Weather
                && part.Purpose == "WeatherReport"
                && part.Status == TalkPartStatus.Rendered
                && part.AnnouncementId != null)
            .Select(part => part.AnnouncementId!.Value)
            .ToListAsync(ct);

        return weatherAnnouncementIds.Count > 0
            && await ShowRunnerService
                .ImmediatePlayableAnnouncements(db.Announcements.AsNoTracking(), weatherAnnouncementIds, freshCutoff)
                .AnyAsync(ct);
    }

    private async Task<Moderator> ResolveWeatherModeratorAsync(
        StationSettings settings,
        Moderator fallback,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (settings.WeatherSpecialistModeratorId is int specialistId)
        {
            var configured = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == specialistId && m.IsActive && m.IsWeatherSpecialist, ct);
            if (configured is not null)
            {
                return configured;
            }
        }

        return await db.Moderators.AsNoTracking()
            .Where(m => m.IsActive && m.IsWeatherSpecialist)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct)
            ?? fallback;
    }

    /// <summary>
    /// Reads the mailbag: the host's mood (talkativeness) decides how many queued
    /// messages (1–10) get woven into ONE on-air segment. The rest wait for the
    /// next cycle, so a reserved host hands them out one by one.
    /// </summary>
    private async Task<bool> TryProduceGreetingAsync(
        AnnouncementFactory factory, ShowContext context, string stationName, CancellationToken ct)
    {
        var moderator = context.Moderator;
        var talkativeness = TalkPlanner.EffectiveTalkativeness(moderator, context.Format);
        var batchSize = TalkPlanner.PickGreetingBatchSize(Random.Shared, talkativeness);

        // Requests whose track is in production are NOT read here — they air as a
        // dedication right before their song. Only greetings, requests nobody could
        // pin a genre on, and stale requests (production too slow) hit the mailbag.
        var staleCutoff = DateTime.UtcNow.AddMinutes(-RequestFulfillmentTimeoutMinutes);
        List<ListenerMessage> messages;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            messages = await db.ListenerMessages
                .Where(m => m.Status == ListenerMessageStatus.Queued
                    && m.FulfilledByTrackId == null
                    && (m.Kind == ListenerMessageKind.Greeting
                        || m.RequestGenre == null || m.RequestGenre == ""
                        || m.SubmittedAt < staleCutoff))
                .OrderBy(m => m.SubmittedAt)
                .Take(batchSize)
                .ToListAsync(ct);
        }

        if (messages.Count == 0)
        {
            return false;
        }

        var facts = string.Join("\n", messages.Select(FormatMessageFact));
        var lengthHint = messages.Count == 1
            ? "3-5 sentences."
            : $"Cover all {messages.Count} messages, roughly 2-3 sentences each.";

        var announcement = await factory.ProduceAsync(
            AnnouncementKind.ListenerGreeting, moderator, null, facts, stationName, ct, lengthHint);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var ids = messages.Select(m => m.Id).ToList();
            await db.ListenerMessages
                .Where(m => ids.Contains(m.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, ListenerMessageStatus.OnAir)
                    .SetProperty(m => m.ModeratorId, moderator.Id)
                    .SetProperty(m => m.AnnouncementId, announcement.Id), ct);
        }

        logger.LogInformation(
            "Greeting segment produced: {Count} message(s) read by {Moderator}",
            messages.Count, moderator.Name);
        return true;
    }

    private static string FormatMessageFact(ListenerMessage m)
    {
        // Requests only reach the mailbag when their song could NOT be delivered
        // (no recognizable genre, or production didn't finish in time).
        if (m.Kind == ListenerMessageKind.Request)
        {
            var wish = string.IsNullOrWhiteSpace(m.RequestGenre) ? "" : $", wished for {m.RequestGenre}";
            return $"- {m.SenderName} (music request{wish} — the song is NOT available): \"{m.MessageText}\"";
        }

        return $"- {m.SenderName}: \"{m.MessageText}\"";
    }
}
