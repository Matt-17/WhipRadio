using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>
/// Invalidates the <see cref="StationSettingsCache"/> whenever a SaveChanges
/// commits on a context that tracks a <see cref="StationSettings"/> entity, so
/// hot paths reading through the cache (playout On Air / mixer toggles) react
/// immediately instead of after the cache TTL. Tracking a settings row without
/// changing it triggers a spurious invalidation, which only costs one extra
/// single-row read.
/// </summary>
public sealed class StationSettingsCacheInvalidationInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        InvalidateIfSettingsTracked(eventData);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        InvalidateIfSettingsTracked(eventData);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void InvalidateIfSettingsTracked(SaveChangesCompletedEventData eventData)
    {
        if (eventData.Context?.ChangeTracker.Entries<StationSettings>().Any() == true)
        {
            services.GetService<StationSettingsCache>()?.Invalidate();
        }
    }
}
