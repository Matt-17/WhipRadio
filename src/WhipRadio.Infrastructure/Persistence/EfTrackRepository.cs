using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

public class EfTrackRepository(RadioDbContext db) : ITrackRepository
{
    public async Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct)
        => await db.Tracks.AsNoTracking().Where(t => !t.IsRetired).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetRecentlyPlayedTrackIdsAsync(int count, CancellationToken ct)
        => await db.PlayLog.AsNoTracking()
            .Where(e => e.ItemType == PlayoutItemType.Track)
            .OrderByDescending(e => e.PlayedAt)
            .Take(count)
            .Select(e => e.ItemId)
            .ToListAsync(ct);
}
