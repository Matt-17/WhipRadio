namespace WhipRadio.Core.Playout;

public static class TopOfHourScheduler
{
    public const int DefaultPrepareAheadMinutes = 10;

    public static int NormalizeCadence(int cadenceMinutes)
        => Math.Clamp(cadenceMinutes, 15, 24 * 60);

    public static double NormalizeFadeOutSeconds(double seconds)
        => double.IsFinite(seconds) ? Math.Clamp(seconds, 0.25, 10) : 1.0;

    public static int NormalizeIntroGraceSeconds(int seconds)
        => Math.Clamp(seconds, 0, 60);

    public static DateTimeOffset NextTarget(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var nextMinute = minuteOfDay - minuteOfDay % cadence + cadence;
        return new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            localNow.Offset).AddMinutes(nextMinute);
    }

    public static DateTimeOffset CurrentTarget(DateTimeOffset localNow, int cadenceMinutes)
    {
        var cadence = NormalizeCadence(cadenceMinutes);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var targetMinute = minuteOfDay - minuteOfDay % cadence;
        return new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            localNow.Offset).AddMinutes(targetMinute);
    }

    public static DateTimeOffset NextPreparationTarget(
        DateTimeOffset localNow,
        int cadenceMinutes,
        int prepareAheadMinutes = DefaultPrepareAheadMinutes)
    {
        var next = NextTarget(localNow, cadenceMinutes);
        return next - localNow <= TimeSpan.FromMinutes(Math.Clamp(prepareAheadMinutes, 1, 60))
            ? next
            : DateTimeOffset.MinValue;
    }
}
