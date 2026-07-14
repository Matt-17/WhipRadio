using ATL;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Metadata;

namespace WhipRadio.Infrastructure.Metadata;

/// <summary>
/// Tag reader over ATL (z440.atl.core, MIT): ID3v1/v2 incl. MusicBrainz TXXX
/// identifiers and ISRC, plus WAV RIFF INFO. Failure-soft — an unreadable or
/// tagless file yields empty tags, never an exception.
/// </summary>
public sealed class AtlFileTagReader(ILogger<AtlFileTagReader> logger) : IFileTagReader
{
    public FileTags Read(string absolutePath)
    {
        try
        {
            var track = new ATL.Track(absolutePath);
            return new FileTags(
                Title: NullIfEmpty(track.Title),
                Artist: NullIfEmpty(track.Artist),
                AlbumArtist: NullIfEmpty(track.AlbumArtist),
                Album: NullIfEmpty(track.Album),
                TrackNumber: track.TrackNumber,
                DiscNumber: track.DiscNumber,
                Year: track.Year is > 0 ? track.Year : null,
                Genre: NullIfEmpty(track.Genre),
                Isrc: NullIfEmpty(track.ISRC) ?? AdditionalField(track, "TSRC", "ISRC"),
                MusicBrainzArtistId: AdditionalField(track, "MUSICBRAINZ ARTIST ID", "MusicBrainz Artist Id"),
                MusicBrainzRecordingId: AdditionalField(track, "MUSICBRAINZ RECORDING ID", "MUSICBRAINZ TRACK ID", "MusicBrainz Track Id"),
                MusicBrainzReleaseId: AdditionalField(track, "MUSICBRAINZ ALBUM ID", "MusicBrainz Album Id"),
                MusicBrainzReleaseGroupId: AdditionalField(track, "MUSICBRAINZ RELEASE GROUP ID", "MusicBrainz Release Group Id"),
                DurationSeconds: track.DurationMs > 0 ? track.DurationMs / 1000.0 : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading tags failed for {Path}; importing without tags", absolutePath);
            return new FileTags();
        }
    }

    private static string? AdditionalField(ATL.Track track, params string[] names)
    {
        foreach (var pair in track.AdditionalFields)
        {
            foreach (var name in names)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
