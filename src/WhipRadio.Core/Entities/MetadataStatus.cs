namespace WhipRadio.Core.Entities;

/// <summary>
/// Enrichment state of an imported track's metadata (Phase 6a). Hosts may only
/// make factual on-air claims about tracks whose metadata is trustworthy
/// (<see cref="Matched"/> and above); ambiguous matches stay playable but keep
/// their file-tag identity until reviewed.
/// </summary>
public enum MetadataStatus
{
    /// <summary>Not an enrichment subject (generated tracks).</summary>
    None = 0,

    /// <summary>File tags and audio analysis only; not yet (or not) matched.</summary>
    LocalOnly = 1,

    /// <summary>Matched with a strong anchor at very high confidence; applied automatically.</summary>
    AutoMatched = 2,

    /// <summary>Matched with good confidence; safe fields applied, review badge shown.</summary>
    Matched = 3,

    /// <summary>Several plausible candidates; stored for review, display fields untouched.</summary>
    Ambiguous = 4,

    /// <summary>No trustworthy candidate; local tags only.</summary>
    NeedsReview = 5,

    /// <summary>User accepted a match.</summary>
    Verified = 6,

    /// <summary>User rejected all candidates; stays local, enrichment skips it.</summary>
    Rejected = 7,
}
