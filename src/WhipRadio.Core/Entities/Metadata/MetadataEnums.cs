namespace WhipRadio.Core.Entities.Metadata;

/// <summary>What kind of entity a metadata record belongs to.</summary>
public enum MetadataOwnerType
{
    Track = 0,
    Artist = 1,
}

/// <summary>
/// License class of a metadata claim (Phase 6a §6.2). Default enrichment only
/// applies claims whose class is safe for an open-source project with possible
/// commercial use.
/// </summary>
public enum MetadataLicenseClass
{
    Unknown = 0,
    FileProvided = 1,
    UserProvided = 2,
    CC0 = 3,
    OwnAnalysis = 4,
}

/// <summary>Review state of a stored match candidate.</summary>
public enum CandidateStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}
