using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Metadata;

/// <summary>Local evidence for one imported track (tags + heuristics + probe).</summary>
public sealed record TrackMatchEvidence(
    string? Title,
    string? Artist,
    string? Album = null,
    int? TrackNumber = null,
    int? Year = null,
    double? DurationSeconds = null,
    string? Isrc = null,
    string? MusicBrainzRecordingId = null);

/// <summary>One external recording candidate (MusicBrainz shape).</summary>
public sealed record RecordingCandidate(
    string RecordingId,
    string Title,
    string Artist,
    string? ArtistId = null,
    string? Album = null,
    int? Year = null,
    int? TrackNumber = null,
    double? DurationSeconds = null,
    IReadOnlyList<string>? Isrcs = null);

public sealed record MatchScore(double Score, IReadOnlyList<string> Reasons, bool HasStrongAnchor);

/// <summary>
/// Confidence scoring for imported-track identity (Phase 6a §5). Pure math —
/// no I/O. Strong anchors (embedded MBID, ISRC, full artist+title+album+track
/// agreement) are required for auto-acceptance; fuzzy similarity alone caps
/// below the auto-match band so bad tags are never silently misidentified.
/// </summary>
public static class MetadataMatchScorer
{
    public const double AutoMatchThreshold = 0.95;
    public const double MatchedThreshold = 0.80;
    public const double AmbiguousThreshold = 0.55;

    private const double DurationToleranceSeconds = 3.0;
    /// <summary>Fuzzy-only scores stay below the auto-match band by design.</summary>
    private const double FuzzyScoreCap = 0.94;

    public static MatchScore Score(TrackMatchEvidence local, RecordingCandidate candidate)
    {
        var reasons = new List<string>();
        var durationClose = DurationCloseness(local.DurationSeconds, candidate.DurationSeconds);
        var titleSim = Similarity(local.Title, candidate.Title);
        var artistSim = Similarity(local.Artist, candidate.Artist);
        // A missing album is unknown, not a contradiction — score it neutral.
        // The full-agreement anchor below still requires a real album match.
        var albumSim = local.Album is null || candidate.Album is null
            ? 0.5
            : Similarity(local.Album, candidate.Album);

        // Strong anchor 1: the file itself carries the candidate's recording MBID.
        if (!string.IsNullOrWhiteSpace(local.MusicBrainzRecordingId)
            && string.Equals(local.MusicBrainzRecordingId, candidate.RecordingId, StringComparison.OrdinalIgnoreCase)
            && durationClose >= 0.5)
        {
            reasons.Add("embedded MusicBrainz recording id matches");
            return new MatchScore(0.98, reasons, HasStrongAnchor: true);
        }

        // Strong anchor 2: ISRC match with roughly matching artist/title.
        if (!string.IsNullOrWhiteSpace(local.Isrc)
            && candidate.Isrcs?.Contains(local.Isrc, StringComparer.OrdinalIgnoreCase) == true
            && (titleSim + artistSim) / 2 >= 0.5)
        {
            reasons.Add("ISRC matches with plausible artist/title");
            return new MatchScore(0.96, reasons, HasStrongAnchor: true);
        }

        // Strong anchor 3: full agreement across artist, title, album, track number
        // and duration — one specific release position.
        if (titleSim >= 0.9 && artistSim >= 0.9
            && local.Album is not null && candidate.Album is not null && albumSim >= 0.9
            && local.TrackNumber is { } trackNo && trackNo == candidate.TrackNumber
            && durationClose >= 1.0)
        {
            reasons.Add("artist, title, album, track number and duration all match");
            return new MatchScore(0.95, reasons, HasStrongAnchor: true);
        }

        // Composite fuzzy score (medium/weak signals only).
        AddReason(reasons, titleSim, "title");
        AddReason(reasons, artistSim, "artist");
        AddReason(reasons, albumSim, "album");
        var yearClose = YearCloseness(local.Year, candidate.Year);
        if (durationClose >= 1.0)
        {
            reasons.Add("duration matches");
        }

        var score = titleSim * 0.40
            + artistSim * 0.30
            + albumSim * 0.10
            + durationClose * 0.15
            + yearClose * 0.05;
        return new MatchScore(Math.Min(FuzzyScoreCap, score), reasons, HasStrongAnchor: false);
    }

    /// <summary>Confidence bands per the brief §5.1; auto-accept needs a strong anchor.</summary>
    public static MetadataStatus Classify(MatchScore match) => match switch
    {
        { Score: >= AutoMatchThreshold, HasStrongAnchor: true } => MetadataStatus.AutoMatched,
        { Score: >= MatchedThreshold } => MetadataStatus.Matched,
        { Score: >= AmbiguousThreshold } => MetadataStatus.Ambiguous,
        _ => MetadataStatus.NeedsReview,
    };

    private static void AddReason(List<string> reasons, double similarity, string field)
    {
        if (similarity >= 0.9)
        {
            reasons.Add($"{field} matches");
        }
        else if (similarity >= 0.6)
        {
            reasons.Add($"{field} is similar");
        }
    }

    private static double DurationCloseness(double? local, double? candidate)
    {
        if (local is not { } a || a <= 0 || candidate is not { } b || b <= 0)
        {
            return 0.5; // unknown — neither evidence nor contradiction
        }

        var delta = Math.Abs(a - b);
        if (delta <= DurationToleranceSeconds)
        {
            return 1.0;
        }

        // Linear falloff to zero at 30 s difference.
        return Math.Max(0, 1 - (delta - DurationToleranceSeconds) / 27.0);
    }

    private static double YearCloseness(int? local, int? candidate)
        => local is { } a && candidate is { } b
            ? Math.Abs(a - b) switch { 0 => 1.0, 1 => 0.8, <= 3 => 0.5, _ => 0.0 }
            : 0.5;

    /// <summary>Normalized-string similarity: 1 − Levenshtein/maxLength.</summary>
    public static double Similarity(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var left = FilenameHeuristics.NormalizeForMatching(a);
        var right = FilenameHeuristics.NormalizeForMatching(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left == right)
        {
            return 1;
        }

        var distance = Levenshtein(left, right);
        var levenshteinSimilarity = 1.0 - distance / (double)Math.Max(left.Length, right.Length);

        // Containment ("Teardrop" in "Teardrop (Live at ...)") reads as a
        // plausible variant, which pure edit distance punishes for the extra
        // length — keep such pairs in the review-worthy band.
        var (shorter, longer) = left.Length <= right.Length ? (left, right) : (right, left);
        if (shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal))
        {
            return Math.Max(levenshteinSimilarity, 0.5 + 0.5 * shorter.Length / longer.Length);
        }

        return levenshteinSimilarity;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
