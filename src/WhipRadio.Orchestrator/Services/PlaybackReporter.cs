using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Callback target for the PlayoutService: play log, counters, now-playing.</summary>
public interface IPlaybackReporter
{
    Task ReportStartedAsync(PlayoutItem item, CancellationToken ct);

    void ReportIdle();
}

public class PlaybackReporter(
    IDbContextFactory<RadioDbContext> dbFactory,
    INowPlayingState nowPlaying,
    ILogger<PlaybackReporter> logger) : IPlaybackReporter
{
    public async Task ReportStartedAsync(PlayoutItem item, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.PlayLog.Add(new PlayLogEntry
        {
            PlayedAt = DateTime.UtcNow,
            ItemType = item.ItemType,
            ItemId = item.ItemId,
            ModeratorId = item.ModeratorId,
        });

        if (item.ItemType == PlayoutItemType.Track)
        {
            await db.Tracks
                .Where(t => t.Id == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayCount, t => t.PlayCount + 1), ct);
        }
        else
        {
            await db.Announcements
                .Where(a => a.Id == item.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WasPlayed, true), ct);
        }

        await db.SaveChangesAsync(ct);

        string? moderatorName = null;
        if (item.ModeratorId is int moderatorId)
        {
            moderatorName = (await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == moderatorId, ct))?.Name;
        }

        nowPlaying.SetCurrent(new NowPlayingInfo(
            item.ItemType, item.ItemId, item.Title, DateTime.UtcNow, item.DurationSeconds, moderatorName));

        logger.LogInformation("On air: {Type} \"{Title}\"", item.ItemType, item.Title);
    }

    public void ReportIdle() => nowPlaying.SetCurrent(null);
}
