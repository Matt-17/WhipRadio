namespace WhipRadio.Core.Metadata;

/// <summary>
/// Tags read from an audio file. Evidence, not verified truth — the user's
/// files may carry wrong tags (Phase 6a §4.3).
/// </summary>
public sealed record FileTags(
    string? Title = null,
    string? Artist = null,
    string? AlbumArtist = null,
    string? Album = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    int? Year = null,
    string? Genre = null,
    string? Isrc = null,
    string? MusicBrainzArtistId = null,
    string? MusicBrainzRecordingId = null,
    string? MusicBrainzReleaseId = null,
    string? MusicBrainzReleaseGroupId = null,
    double? DurationSeconds = null);

/// <summary>Reads embedded tags from a WAV/MP3 file; failure-soft (empty tags on error).</summary>
public interface IFileTagReader
{
    FileTags Read(string absolutePath);
}
