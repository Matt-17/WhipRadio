namespace WhipRadio.Core.Selection;

/// <summary>
/// One recently played track, light enough for the selector to reason about
/// artist/subgenre repetition without reloading full Track entities. Ordered
/// newest-first by the repository.
/// </summary>
public sealed record PlayedTrackRef(
    Guid TrackId,
    Guid? ArtistId,
    string? Subgenre,
    DateTime PlayedAt,
    string? ImportedArtist = null);
