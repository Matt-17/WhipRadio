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
        if (await TryProduceGreetingAsync(factory, moderator, stationName, ct))
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

    private async Task<bool> TryProduceGreetingAsync(
        AnnouncementFactory factory, Moderator moderator, string stationName, CancellationToken ct)
    {
        ListenerMessage? message;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            message = await db.ListenerMessages
                .Where(m => m.Status == ListenerMessageStatus.Queued)
                .OrderBy(m => m.SubmittedAt)
                .FirstOrDefaultAsync(ct);
        }

        if (message is null)
        {
            return false;
        }

        var announcement = await factory.ProduceAsync(
            AnnouncementKind.ListenerGreeting, moderator, null,
            $"{message.SenderName}|{message.MessageText}", stationName, ct);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.ListenerMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, ListenerMessageStatus.OnAir)
                    .SetProperty(m => m.ModeratorId, moderator.Id)
                    .SetProperty(m => m.AnnouncementId, announcement.Id), ct);
        }

        logger.LogInformation("Listener greeting from {Sender} produced for air", message.SenderName);
        return true;
    }
}
