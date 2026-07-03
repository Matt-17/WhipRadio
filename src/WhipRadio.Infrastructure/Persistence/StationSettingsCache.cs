using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Hot-path read access to StationSettings with a short TTL, so routers
/// can react to settings changes without hammering the database.</summary>
public class StationSettingsCache(IDbContextFactory<RadioDbContext> dbFactory, TimeProvider timeProvider)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    // Settings + load time travel together in one immutable snapshot so the
    // lock-free fast path can never observe a torn timestamp/value pair.
    private sealed record Snapshot(StationSettings Settings, DateTimeOffset LoadedAt);

    private Snapshot _snapshot = new(new StationSettings(), DateTimeOffset.MinValue);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<StationSettings> GetAsync(CancellationToken ct)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (timeProvider.GetUtcNow() - snapshot.LoadedAt < Ttl)
        {
            return snapshot.Settings;
        }

        await _gate.WaitAsync(ct);
        try
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (timeProvider.GetUtcNow() - snapshot.LoadedAt >= Ttl)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
                snapshot = new Snapshot(settings, timeProvider.GetUtcNow());
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot.Settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the next read to hit the database. Called by
    /// <see cref="StationSettingsCacheInvalidationInterceptor"/> after a save that
    /// touched StationSettings, so toggles like On Air react immediately instead
    /// of after the TTL.</summary>
    public void Invalidate()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, snapshot with { LoadedAt = DateTimeOffset.MinValue });
    }
}
