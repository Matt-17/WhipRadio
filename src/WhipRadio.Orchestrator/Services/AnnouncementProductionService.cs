using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Keeps a small, varied announcement pool warm for the current host:
/// song intros for upcoming tracks, a weather report every 4th cycle, and the
/// occasional personal note so the show doesn't sound like a jukebox.
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

        var context = await schedule.GetCurrentAsync(ct);
        var moderator = context.Moderator;

        string stationName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            stationName = (await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.StationName ?? "WhipRadio";
        }

        var cycle = Interlocked.Increment(ref _cycleCounter);

        // Every 4th cycle: weather instead of a song intro.
        if (cycle % 4 == 0)
        {
            if (!await HasUnplayedAsync(AnnouncementKind.Weather, moderator.Id, ct))
            {
                var facts = await weatherSource.GetSummaryAsync(moderator.Language, ct);
                await factory.ProduceAsync(AnnouncementKind.Weather, moderator, null, facts, stationName, ct);
            }

            return;
        }

        // Every 7th cycle: a personal note drawing on the host's day memory.
        if (cycle % 7 == 0)
        {
            if (!await HasUnplayedAsync(AnnouncementKind.PersonalNote, moderator.Id, ct))
            {
                await factory.ProduceAsync(AnnouncementKind.PersonalNote, moderator, null, null, stationName, ct);
            }

            return;
        }

        // Default: make sure the next planned track has an unplayed intro.
        var nextTrack = await selector.PickNextAsync(context, ct);
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

            // Load the artist for the intro prompt.
            nextTrack = await db.Tracks.AsNoTracking()
                .Include(t => t.Artist)
                .FirstAsync(t => t.Id == nextTrack.Id, ct);
        }

        await factory.ProduceAsync(AnnouncementKind.SongIntro, moderator, nextTrack, null, stationName, ct);
    }

    private async Task<bool> HasUnplayedAsync(AnnouncementKind kind, int moderatorId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Announcements.AnyAsync(
            a => a.Kind == kind && a.ModeratorId == moderatorId && !a.WasPlayed, ct);
    }
}
