using WhipRadio.Core.Entities;

namespace WhipRadio.Orchestrator.Services;

public static class ProductionSpecialistPolicy
{
    public static Moderator ResolveNewsModerator(
        StationSettings settings,
        IEnumerable<Moderator> moderators,
        Moderator fallback)
    {
        var active = moderators.Where(m => m.IsActive).ToList();
        if (settings.NewsPresenterModeratorId is int configuredId)
        {
            var configured = active.FirstOrDefault(m => m.Id == configuredId && m.IsNewsSpecialist);
            if (configured is not null)
            {
                return configured;
            }
        }

        return active.FirstOrDefault(m => m.IsNewsSpecialist)
            ?? active.FirstOrDefault(m => string.Equals(m.Name, "Maya Current", StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    public static Moderator? ResolveWeatherModerator(
        StationSettings settings,
        IEnumerable<Moderator> moderators,
        Moderator newsModerator)
    {
        var active = moderators.Where(m => m.IsActive && m.IsWeatherSpecialist && m.Id != newsModerator.Id).ToList();
        if (settings.WeatherSpecialistModeratorId is int configuredId)
        {
            var configured = active.FirstOrDefault(m => m.Id == configuredId);
            if (configured is not null)
            {
                return configured;
            }
        }

        return active.OrderBy(m => m.Id).FirstOrDefault();
    }

    public static string? BuildWarning(StationSettings settings, IEnumerable<Moderator> moderators)
    {
        var active = moderators.Where(m => m.IsActive).ToList();
        var newsModerator = settings.NewsPresenterModeratorId is int configuredNewsId
            ? active.FirstOrDefault(m => m.Id == configuredNewsId && m.IsNewsSpecialist)
            : null;
        newsModerator ??= active.FirstOrDefault(m => m.IsNewsSpecialist);

        if (settings.NewsEnabled && newsModerator is null)
        {
            return "No active news specialist is assigned; create or activate a news host before the next package.";
        }

        if (!settings.WeatherEnabled)
        {
            return null;
        }

        return ResolveWeatherModerator(settings, active, newsModerator ?? new Moderator { Id = int.MinValue }) is null
            ? "Weather is enabled, but no distinct active weather specialist is available; weather will be skipped in top-of-hour packages."
            : null;
    }
}
