using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Keeps a small announcement pool warm: produces a SongIntro for the next
/// planned track; every 4th cycle produces a Weather announcement instead.
/// </summary>
public class AnnouncementProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    ILogger<AnnouncementProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);
    private int _cycleCounter;

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
        var selector = scope.ServiceProvider.GetRequiredService<ITrackSelector>();
        var weatherSource = scope.ServiceProvider.GetRequiredService<IAnnouncementDataSource>();

        var (slot, moderator) = await schedule.GetCurrentAsync(ct);

        string stationName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            stationName = (await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.StationName ?? "WhipRadio";
        }

        // Every 4th announcement cycle: weather instead of a song intro.
        if (Interlocked.Increment(ref _cycleCounter) % 4 == 0)
        {
            if (await HasUnplayedAnnouncementAsync(AnnouncementKind.Weather, ct))
            {
                return;
            }

            var facts = await weatherSource.GetSummaryAsync(moderator.Language, ct);
            await factory.ProduceAsync(AnnouncementKind.Weather, moderator, null, facts, stationName, ct);
            return;
        }

        // Peek the next planned track and make sure it has an unplayed intro.
        var nextTrack = await selector.PickNextAsync(slot, moderator, ct);
        if (nextTrack is null)
        {
            return; // cold start: ShowRunner produces filler talk on its own
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var hasIntro = await db.Announcements.AnyAsync(
                a => a.Kind == AnnouncementKind.SongIntro && a.RelatedTrackId == nextTrack.Id && !a.WasPlayed, ct);
            if (hasIntro)
            {
                return;
            }
        }

        await factory.ProduceAsync(AnnouncementKind.SongIntro, moderator, nextTrack, null, stationName, ct);
    }

    private async Task<bool> HasUnplayedAnnouncementAsync(AnnouncementKind kind, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Announcements.AnyAsync(a => a.Kind == kind && !a.WasPlayed, ct);
    }
}
