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
    ILogger<AnnouncementProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);

    /// <summary>After this, an unfulfilled request goes to the mailbag with an honest "not available".</summary>
    private const int RequestFulfillmentTimeoutMinutes = 20;

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
        var weatherSource = scope.ServiceProvider.GetRequiredService<IAnnouncementDataSource>();

        var context = await schedule.GetCurrentAsync(ct);
        var moderator = context.Moderator;

        string stationName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            stationName = (await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.StationName ?? "WhipRadio";
        }

        // Queued listener greetings jump the line — listeners are waiting.
        if (await TryProduceGreetingAsync(factory, context, stationName, ct))
        {
            return;
        }

        // Weather is hourly, on the full hour: prepare a FRESH report in the last
        // minutes of the hour so it's ready to air right after the top.
        var minute = timeProvider.GetLocalNow().Minute;
        var freshCutoff = DateTime.UtcNow.AddMinutes(-30);
        if (WeatherScheduler.ShouldPrepare(minute)
            && !await HasFreshUnplayedWeatherAsync(freshCutoff, ct))
        {
            var facts = await weatherSource.GetSummaryAsync(moderator.Language, ct);
            await factory.ProduceAsync(AnnouncementKind.Weather, moderator, null, facts, stationName, ct);
        }
    }

    private async Task<bool> HasFreshUnplayedWeatherAsync(DateTime freshCutoff, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Announcements.AnyAsync(
            a => a.Kind == AnnouncementKind.Weather && !a.WasPlayed && a.CreatedAt >= freshCutoff, ct);
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
        var talkativeness = TalkPlanner.EffectiveTalkativeness(moderator.Talkativeness, context.Format?.Talkativeness);
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
