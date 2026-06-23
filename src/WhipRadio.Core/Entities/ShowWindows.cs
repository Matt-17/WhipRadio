namespace WhipRadio.Core.Entities;

/// <summary>
/// Time windows for the current and previous show, resolved from the weekly
/// program grid (or the fallback 2-hour shift rotation). Used by the track
/// selector to hard-exclude repeats and by the prompt builder to show the host
/// what already aired. All timestamps are UTC.
/// </summary>
public sealed record ShowWindows(
    DateTime CurrentStartUtc,
    DateTime CurrentEndUtc,
    DateTime? PreviousStartUtc,
    DateTime? PreviousEndUtc,
    string? CurrentFormatName,
    string? PreviousFormatName)
{
    /// <summary>
    /// Earliest UTC moment whose plays should be hard-excluded: the start of the
    /// previous show when known, otherwise the start of the current show. This is
    /// the "do not replay anything from the current or previous show" cutoff.
    /// </summary>
    public DateTime ExclusionSinceUtc => PreviousStartUtc ?? CurrentStartUtc;
}
