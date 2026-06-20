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

    public static bool ShouldPrepare(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var minutesUntilNext = cadence - minuteOfDay % cadence;
        return minutesUntilNext is > 0 and <= 10;
    }

    public static bool IsAirWindow(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        return minuteOfDay % cadence < 10;
    }

    public static DateTimeOffset CurrentWindowStart(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var startMinute = minuteOfDay - minuteOfDay % cadence;
        return new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            localNow.Offset).AddMinutes(startMinute);
    }

    public static DateTimeOffset NextWindowStart(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var startMinute = minuteOfDay - minuteOfDay % cadence + cadence;
        return new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            localNow.Offset).AddMinutes(startMinute);
    }

    public static int NormalizeCadence(int cadenceMinutes)
        => Math.Clamp(cadenceMinutes, 15, 180);
}
