using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Persistence;

public class EfTrackRepository(RadioDbContext db, StationSettingsCache settingsCache) : ITrackRepository
{
    public async Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct)
    {
        // Imported (uploaded/external) tracks rotate only while the Archive
        // playout toggle is on; files missing from an external drive never
        // reach the queue at all.
        var settings = await settingsCache.GetAsync(ct);
        var query = db.Tracks.AsNoTracking()
            .Include(t => t.Artist)
            .Where(t => !t.IsRetired && !t.FileMissing);
        if (!settings.ArchivePlayoutEnabled)
        {
            query = query.Where(t => t.Source == TrackSource.Generated);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetRecentlyPlayedTrackIdsAsync(int count, CancellationToken ct)
        => await db.PlayLog.AsNoTracking()
            .Where(e => e.ItemType == PlayoutItemType.Track)
            .OrderByDescending(e => e.PlayedAt)
            .Take(count)
            .Select(e => e.ItemId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetTrackIdsPlayedSinceAsync(DateTime sinceUtc, int maxCount, CancellationToken ct)
        => await db.PlayLog.AsNoTracking()
            .Where(e => e.ItemType == PlayoutItemType.Track && e.PlayedAt >= sinceUtc)
            .OrderByDescending(e => e.PlayedAt)
            .Take(maxCount)
            .Select(e => e.ItemId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PlayedTrackRef>> GetRecentPlayedRefsAsync(int count, CancellationToken ct)
        => await db.PlayLog.AsNoTracking()
            .Where(e => e.ItemType == PlayoutItemType.Track)
            .OrderByDescending(e => e.PlayedAt)
            .Take(count)
            .Join(db.Tracks.AsNoTracking(),
                entry => entry.ItemId,
                track => track.Id,
                (entry, track) => new { track.Id, track.ArtistId, track.Subgenre, entry.PlayedAt, track.ImportedArtist })
            // A JOIN does not preserve the inner Take's order; re-sort so refs[0] is reliably newest.
            .OrderByDescending(x => x.PlayedAt)
            .Select(x => new PlayedTrackRef(x.Id, x.ArtistId, x.Subgenre, x.PlayedAt, x.ImportedArtist))
            .ToListAsync(ct);
}
