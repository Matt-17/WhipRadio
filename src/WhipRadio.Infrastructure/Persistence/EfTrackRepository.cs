using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Persistence;

public class EfTrackRepository(RadioDbContext db) : ITrackRepository
{
    public async Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct)
        => await db.Tracks.AsNoTracking()
            .Include(t => t.Artist)
            .Where(t => !t.IsRetired)
            .ToListAsync(ct);

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
                (entry, track) => new { track.Id, track.ArtistId, track.Subgenre, entry.PlayedAt })
            // A JOIN does not preserve the inner Take's order; re-sort so refs[0] is reliably newest.
            .OrderByDescending(x => x.PlayedAt)
            .Select(x => new PlayedTrackRef(x.Id, x.ArtistId, x.Subgenre, x.PlayedAt))
            .ToListAsync(ct);
}
