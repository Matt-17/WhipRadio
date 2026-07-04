namespace WhipRadio.Core.Selection;

/// <summary>
/// How a format wants tracks selected. Produced once at format-creation time by
/// an LLM that reads the director's free-text <see cref="Entities.Format.Description"/>
/// (so an "artist feature" can lock to one artist, a "theme block" can lean on a
/// keyword, etc.), then enforced deterministically per pick. Defaults describe the
/// common case: standard rotation with artist-repeat caps and subgenre variety.
/// </summary>
public enum SelectionMode
{
    /// <summary>Normal rotation: genre/subgenre filter, artist caps, subgenre variety.</summary>
    StandardRotation,

    /// <summary>Only the featured artist's tracks play; relaxes to SpotlightArtist then StandardRotation on exhaustion.</summary>
    SingleArtistFeature,

    /// <summary>The featured artist gets a weight boost but others still rotate.</summary>
    SpotlightArtist,

    /// <summary>A keyword theme softly biases selection via the track's Style field.</summary>
    ThemeBlock,

    /// <summary>No genre/subgenre filter at all; anything fair game.</summary>
    Freeform,

    /// <summary>
    /// A scheduled long news show block. Pure discriminator: the slot is filled by a
    /// pre-produced LongFormat news package, not by track selection — the selector
    /// treats this like StandardRotation for whatever plays around the package.
    /// </summary>
    NewsShow,
}

/// <summary>
/// Per-format selection rules. Persisted as an EF owned type on <see cref="Entities.Format"/>.
/// All fields have safe defaults so a format with no planned rules behaves as StandardRotation.
/// </summary>
public sealed record FormatSelectionRules
{
    public SelectionMode Mode { get; set; } = SelectionMode.StandardRotation;

    /// <summary>Artist locked in for SingleArtistFeature / boosted for SpotlightArtist.</summary>
    public Guid? FeaturedArtistId { get; set; }

    /// <summary>Max plays by one artist within the lookback window. null = use StationSettings default.</summary>
    public int? MaxArtistPlaysPerHour { get; set; }

    /// <summary>How many recent plays to consider for the artist-repeat cap.</summary>
    public int ArtistLookbackTracks { get; set; } = 8;

    /// <summary>Avoid the same subgenre back-to-back when alternatives exist.</summary>
    public bool SubgenreRotation { get; set; } = true;

    /// <summary>Apply a weight boost to tracks in the host's PreferredGenres.</summary>
    public bool PreferHostGenres { get; set; } = true;

    /// <summary>Optional target tempo for ThemeBlock / mix continuity (Phase 3 wiring).</summary>
    public double? TargetBpm { get; set; }

    public double? BpmTolerancePct { get; set; }

    /// <summary>Keyword for ThemeBlock mode (matched against Track.Style).</summary>
    public string? Theme { get; set; }

    public static FormatSelectionRules Default => new();
}
