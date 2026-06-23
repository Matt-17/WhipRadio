namespace WhipRadio.Core.Selection;

/// <summary>
/// Station-wide selection knobs projected from <see cref="Entities.StationSettings"/>.
/// Carried on <see cref="Entities.ShowContext"/> so the pure selector stays free of
/// infrastructure and fully unit-testable. <see cref="DiversityEnabled"/> is the master
/// switch: when false, the selector falls back to the legacy last-N-exclusion behavior.
/// </summary>
public sealed record SelectionSettings
{
    public double FatigueFactor { get; init; } = TrackWeighting.DefaultFatigueFactor;

    public int MaxArtistPlaysPerHour { get; init; } = 2;

    public int ArtistLookbackTracks { get; init; } = 8;

    public bool SubgenreRotation { get; init; } = true;

    public bool PreferHostGenres { get; init; } = true;

    public bool DiversityEnabled { get; init; } = true;

    /// <summary>Short recent-exclusion window (the absolute floor that is never relaxed away).</summary>
    public int RecentExclusionCount { get; init; } = 3;

    public static SelectionSettings Default => new();

    public static SelectionSettings Disabled => new() { DiversityEnabled = false };
}
