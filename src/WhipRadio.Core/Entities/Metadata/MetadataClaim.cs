namespace WhipRadio.Core.Entities.Metadata;

/// <summary>
/// One field-level metadata statement with provenance (Phase 6a §6.2): where a
/// value came from, how confident the match was, and whether it was applied to
/// the owner's display fields. Original file tags stay recorded as
/// <see cref="MetadataLicenseClass.FileProvided"/> claims even after an
/// external match is applied, so a bad match can always be undone.
/// </summary>
public class MetadataClaim
{
    public Guid Id { get; set; }

    public MetadataOwnerType OwnerType { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>Logical field, e.g. "Title", "Artist", "Album", "Year", "Isrc".</summary>
    public string FieldName { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>"FileTags", "Filename", "MusicBrainz", "Wikidata", "User".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Source-side identifier (MBID, QID) when applicable.</summary>
    public string? SourceEntityId { get; set; }

    public MetadataLicenseClass LicenseClass { get; set; }

    public double Confidence { get; set; }

    /// <summary>True when the value was written into the owner's display fields.</summary>
    public bool IsApplied { get; set; }

    public DateTime CreatedAt { get; set; }
}
