namespace WhipRadio.Web.Services;

public static class Format
{
    private const string NarrowNoBreakSpace = "\u202F";

    public static string FormatClock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public static string FormatElapsed(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var totalSeconds = (int)Math.Floor(time.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}{NarrowNoBreakSpace}h {minutes:00}{NarrowNoBreakSpace}min {seconds:00}{NarrowNoBreakSpace}s"
            : $"{minutes}{NarrowNoBreakSpace}min {seconds:00}{NarrowNoBreakSpace}s";
    }
}
