namespace WhipRadio.Core.Entities.Metadata;

/// <summary>
/// Stable external identifier attached to a track or artist (Phase 6a §6.3):
/// MusicBrainz MBIDs, Wikidata QIDs, ISRCs. Identifiers are matching anchors
/// and knowledge-base keys, not verified truth by themselves.
/// </summary>
public class ExternalId
{
    public Guid Id { get; set; }

    public MetadataOwnerType OwnerType { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>"MusicBrainz", "Wikidata", "ISRC".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>"Recording", "Artist", "Release", "Qid", "Isrc".</summary>
    public string EntityType { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DateTime CreatedAt { get; set; }
}
