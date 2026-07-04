using System.Globalization;

namespace WhipRadio.Core.Playout;

/// <summary>
/// Wall-clock math for the scheduled long news show blocks (sibling of
/// <see cref="TopOfHourScheduler"/>). Air times are operator-configured local
/// HH:mm values (station setting <c>NewsLongFormatAirTimes</c>) that seed one
/// daily news-show slot each in the program grid.
/// </summary>
public static class LongFormatNewsScheduler
{
    public const int MinDurationMinutes = 30; // ProgramSlot grid minimum
    public const int MaxDurationMinutes = 60;
    public const int DefaultDurationMinutes = 30;

    /// <summary>Tolerant CSV parse: trims, drops garbage, dedupes, sorts.</summary>
    public static IReadOnlyList<TimeOnly> ParseAirTimes(string? csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => TimeOnly.TryParseExact(
                token, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? (TimeOnly?)time
                : null)
            .Where(time => time is not null)
            .Select(time => time!.Value)
            .Distinct()
            .OrderBy(time => time)
            .ToList();

    public static string FormatAirTimes(IEnumerable<TimeOnly> airTimes)
        => string.Join(",", airTimes.Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)));

    public static int NormalizeDurationMinutes(int minutes)
        => minutes <= 0 ? DefaultDurationMinutes : Math.Clamp(minutes, MinDurationMinutes, MaxDurationMinutes);

    /// <summary>Next air time at or after localNow, wrapping past midnight.</summary>
    public static DateTimeOffset? NextTarget(DateTimeOffset localNow, IReadOnlyList<TimeOnly> airTimes)
    {
        if (airTimes.Count == 0)
        {
            return null;
        }

        var today = localNow.Date;
        var nowTime = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        foreach (var time in airTimes.OrderBy(t => t))
        {
            if (time >= nowTime)
            {
                return new DateTimeOffset(today + time.ToTimeSpan(), localNow.Offset);
            }
        }

        var first = airTimes.Min();
        return new DateTimeOffset(today.AddDays(1) + first.ToTimeSpan(), localNow.Offset);
    }

    /// <summary>True when the given local time is exactly one of the configured air times.</summary>
    public static bool IsAirTime(DateTimeOffset targetLocal, IReadOnlyList<TimeOnly> airTimes)
    {
        var time = TimeOnly.FromTimeSpan(targetLocal.TimeOfDay);
        return airTimes.Contains(time);
    }
}
