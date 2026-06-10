namespace WhipRadio.Core.Playout;

/// <summary>
/// Weather airs once per hour, on the full hour only: the producer prepares the
/// report during the last 10 minutes of the hour, the show runner airs it in the
/// first 10 minutes of the next one (the playout is sequential, so "on the full
/// hour" means the first talk slot after it).
/// </summary>
public static class WeatherScheduler
{
    /// <summary>Producer window: get the report ready just before the hour.</summary>
    public static bool ShouldPrepare(int minuteOfHour) => minuteOfHour >= 50;

    /// <summary>Air window: the first talk slot after the full hour.</summary>
    public static bool IsAirWindow(int minuteOfHour) => minuteOfHour < 10;
}
