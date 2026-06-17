using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Hot-path read access to StationSettings with a short TTL, so routers
/// can react to settings changes without hammering SQLite.</summary>
public class StationSettingsCache(IDbContextFactory<RadioDbContext> dbFactory, TimeProvider timeProvider)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
    private StationSettings _cached = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<StationSettings> GetAsync(CancellationToken ct)
    {
        if (timeProvider.GetUtcNow() - _loadedAt < Ttl)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (timeProvider.GetUtcNow() - _loadedAt >= Ttl)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                _cached = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
                _loadedAt = timeProvider.GetUtcNow();
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
