namespace WhipRadio.Core.Playout;

/// <summary>
/// Maps how close a news package is to its target air time onto a GPU scheduling
/// priority. News starts low while there is plenty of lead time and climbs to the
/// top in the final stretch, so an about-to-air bulletin beats routine writer work.
/// </summary>
public static class NewsAirtimeRamp
{
    /// <summary>Minutes-to-air at/under which news takes the highest priority.</summary>
    public const int HighestWithinMinutes = 10;

    /// <summary>Minutes-to-air at/under which news takes medium priority.</summary>
    public const int MediumWithinMinutes = 20;

    /// <summary>
    /// &lt;10 min to air =&gt; Highest, 10–20 min =&gt; Medium, otherwise (incl. already past
    /// target) =&gt; Low. There is deliberately no separate High tier between Medium and
    /// Highest.
    /// </summary>
    public static int Priority(DateTime targetUtc, DateTime nowUtc)
    {
        var minutesToAir = (targetUtc - nowUtc).TotalMinutes;
        if (minutesToAir < HighestWithinMinutes)
        {
            return GpuJobPriority.Highest;
        }

        if (minutesToAir < MediumWithinMinutes)
        {
            return GpuJobPriority.Medium;
        }

        return GpuJobPriority.Low;
    }
}
