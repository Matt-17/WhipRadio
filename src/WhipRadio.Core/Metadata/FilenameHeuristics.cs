using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhipRadio.Core.Metadata;

/// <summary>Low-confidence identity clues parsed from an audio file's path.</summary>
public sealed record FilenameClues(
    string? Artist,
    string? Title,
    string? Album,
    int? TrackNumber);

/// <summary>
/// Conservative file-name/folder heuristics for imported music (Phase 6a §4.4).
/// Clues are query hints and UI suggestions only — they never produce a
/// high-confidence match on their own.
/// </summary>
public static partial class FilenameHeuristics
{
    // "01 - Title", "01. Title", "03 Title" — a separator or whitespace must
    // follow the digits, and the rest must not start with another digit
    // ("2001 A Space Odyssey" stays a plain title).
    [GeneratedRegex(@"^(?<track>\d{1,3})(?:\s*[-._]\s*|\s+)(?<rest>\D.+)$")]
    private static partial Regex TrackNumberPrefix();

    /// <summary>
    /// Parses patterns like "Artist - Title.mp3", "01 - Title.mp3",
    /// "Artist/Album/01 Title.mp3", "Album/Artist - Title.wav".
    /// </summary>
    public static FilenameClues Parse(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath).Trim();
        int? trackNumber = null;

        var numberMatch = TrackNumberPrefix().Match(stem);
        if (numberMatch.Success)
        {
            trackNumber = int.Parse(numberMatch.Groups["track"].Value, CultureInfo.InvariantCulture);
            stem = numberMatch.Groups["rest"].Value.Trim();
        }

        string? artist = null;
        string? title;
        var separatorIndex = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            artist = stem[..separatorIndex].Trim();
            title = stem[(separatorIndex + 3)..].Trim();
        }
        else
        {
            title = stem;
        }

        // Folder shape "Artist/Album/NN Title": with a track number and no
        // artist in the name, the grandparent folder is likely the artist and
        // the parent the album.
        string? album = null;
        var parent = Path.GetFileName(Path.GetDirectoryName(filePath)?.TrimEnd('/', '\\') ?? string.Empty);
        var grandParent = Path.GetFileName(
            Path.GetDirectoryName(Path.GetDirectoryName(filePath)?.TrimEnd('/', '\\') ?? string.Empty)?.TrimEnd('/', '\\')
            ?? string.Empty);
        if (artist is null && trackNumber is not null && !string.IsNullOrEmpty(parent))
        {
            album = parent;
            if (!string.IsNullOrEmpty(grandParent))
            {
                artist = grandParent;
            }
        }

        return new FilenameClues(
            NullIfEmpty(artist),
            NullIfEmpty(title),
            NullIfEmpty(album),
            trackNumber);
    }

    /// <summary>
    /// Match-time normalization (§4.5): trim, unicode-normalize, case-fold,
    /// collapse spaces, unify punctuation variants. Originals stay stored —
    /// this is only for comparing strings.
    /// </summary>
    public static string NormalizeForMatching(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var ch = rune.Value switch
            {
                '‘' or '’' or '´' or '`' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                _ => (char?)null,
            };
            if (ch is not null)
            {
                builder.Append(ch.Value);
                lastWasSpace = false;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(rune.ToString());
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
