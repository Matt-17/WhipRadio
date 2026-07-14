namespace WhipRadio.Core.Entities.Metadata;

/// <summary>
/// One plausible external identity for an imported track (Phase 6a §6.4).
/// Ambiguous matches are stored for review instead of guessed into the
/// library; the review UI accepts/rejects them later.
/// </summary>
public class MetadataCandidate
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public string Source { get; set; } = "MusicBrainz";

    /// <summary>Recording MBID.</summary>
    public string SourceEntityId { get; set; } = string.Empty;

    public string DisplayTitle { get; set; } = string.Empty;

    public string DisplayArtist { get; set; } = string.Empty;

    public string? DisplayAlbum { get; set; }

    public int? DisplayYear { get; set; }

    /// <summary>Artist MBID — the bridge to Wikidata facts once accepted.</summary>
    public string? ArtistEntityId { get; set; }

    public double Score { get; set; }

    /// <summary>JSON array of short human-readable reasons for the score.</summary>
    public string ReasonsJson { get; set; } = "[]";

    public CandidateStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
