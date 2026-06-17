using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

public static class StationSettingsQueries
{
    public static async Task<StationSettings> GetStationSettingsOrDefaultAsync(
        this IQueryable<StationSettings> settings, CancellationToken ct)
        => await settings
            .SingleOrDefaultAsync(s => s.Id == StationSettings.SingletonId, ct)
           ?? new StationSettings();

    public static Task<StationSettings?> FindStationSettingsAsync(
        this IQueryable<StationSettings> settings, CancellationToken ct)
        => settings.SingleOrDefaultAsync(s => s.Id == StationSettings.SingletonId, ct);
}
