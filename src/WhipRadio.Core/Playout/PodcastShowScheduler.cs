namespace WhipRadio.Core.Playout;

/// <summary>
/// Wall-clock math for weekly podcast show slots (sibling of
/// <see cref="LongFormatNewsScheduler"/>): each show owns one weekly grid slot
/// (day-of-week + start minute) whose occurrence must be filled with a
/// produced episode.
/// </summary>
public static class PodcastShowScheduler
{
    public const int MinEpisodeMinutes = 10;
    public const int MaxEpisodeMinutes = 30;
    public const int DefaultEpisodeMinutes = 20;

    public const int MinSlotMinutes = 30; // ProgramSlot grid minimum
    public const int MaxSlotMinutes = 240;

    public static int NormalizeEpisodeMinutes(int minutes)
        => minutes <= 0 ? DefaultEpisodeMinutes : Math.Clamp(minutes, MinEpisodeMinutes, MaxEpisodeMinutes);

    public static int NormalizeSlotMinutes(int minutes, int episodeMinutes)
        => Math.Clamp(Math.Max(minutes, NormalizeEpisodeMinutes(episodeMinutes)), MinSlotMinutes, MaxSlotMinutes);

    /// <summary>Next weekly occurrence of (dayOfWeek, startMinute) at or after localNow.</summary>
    public static DateTimeOffset NextOccurrence(DateTimeOffset localNow, int dayOfWeek, int startMinute)
    {
        var day = ((dayOfWeek % 7) + 7) % 7;
        var minute = Math.Clamp(startMinute, 0, (24 * 60) - 1);

        var daysAhead = (day - (int)localNow.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(localNow.Date, localNow.Offset)
            .AddDays(daysAhead)
            .AddMinutes(minute);
        return candidate >= localNow ? candidate : candidate.AddDays(7);
    }

    /// <summary>True when the given local time is exactly the show's weekly slot start.</summary>
    public static bool IsOccurrenceAt(DateTimeOffset targetLocal, int dayOfWeek, int startMinute)
        => (int)targetLocal.DayOfWeek == ((dayOfWeek % 7) + 7) % 7
            && targetLocal.Hour * 60 + targetLocal.Minute == Math.Clamp(startMinute, 0, (24 * 60) - 1)
            && targetLocal.Second == 0;
}
