using System.Text.RegularExpressions;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Normalizes LLM-produced speech-marker text to the contract consumed by the TTS sidecar:
/// [pause:NNNms] (clamped 100–1500), [breath] (duplicates collapsed),
/// [rate:slow|normal|fast]. Unknown bracket tags are stripped.
/// </summary>
public static partial class SpeechMarkerNormalizer
{
    public const int MinPauseMs = 100;
    public const int MaxPauseMs = 1500;

    /// <summary>Models tend to ask for pauses that play back a touch too long; every
    /// requested pause is scaled by this factor before clamping.</summary>
    private const double PauseScale = 0.65;

    /// <summary>Pre-scale pause inserted at paragraph / news-item boundaries so the TTS
    /// doesn't run straight from one paragraph into the next.</summary>
    private const int ParagraphPauseRawMs = 1000;

    [GeneratedRegex(@"\[(?<tag>[^\[\]]*)\]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"[ \t]*\r?\n[ \t\r\n]*")]
    private static partial Regex ParagraphBreakRegex();

    [GeneratedRegex(@"(?:\[pause:\d+ms\][ \t]*){2,}")]
    private static partial Regex ConsecutivePausesRegex();

    [GeneratedRegex(@"\[breath\](?:\s*\[breath\])+")]
    private static partial Regex DuplicateBreathRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    private static readonly string[] ValidRates = ["slow", "normal", "fast"];

    public static string Normalize(string text) => Normalize(text, allowBreath: true);

    /// <param name="allowBreath">When false, [breath] markers are stripped entirely
    /// (some TTS engines render them badly — station setting).</param>
    public static string Normalize(string text, bool allowBreath)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Paragraph / news-item breaks become an explicit (longer) pause so the TTS
        // doesn't glide from one paragraph straight into the next.
        var normalized = ParagraphBreakRegex().Replace(text, $" [pause:{ParagraphPauseRawMs}ms] ");
        normalized = BracketTagRegex().Replace(normalized, match => NormalizeTag(match.Groups["tag"].Value));
        normalized = DuplicateBreathRegex().Replace(normalized, "[breath]");
        // A model-placed pause landing next to a paragraph pause would stack — keep one.
        normalized = ConsecutivePausesRegex().Replace(normalized, match => CollapsePauses(match.Value));
        if (!allowBreath)
        {
            normalized = normalized.Replace("[breath]", " ");
        }

        normalized = MultiSpaceRegex().Replace(normalized, " ");
        return normalized.Trim();
    }

    /// <summary>
    /// Renders marked-up text for engines without marker support (e.g. cloud TTS):
    /// pauses become ellipses, breath/rate markers are dropped.
    /// </summary>
    public static string ToPlainText(string markedUpText)
    {
        var text = BracketTagRegex().Replace(markedUpText, match =>
        {
            var tag = match.Groups["tag"].Value.Trim().ToLowerInvariant();
            return tag.StartsWith("pause", StringComparison.Ordinal) ? "…" : string.Empty;
        });
        return MultiSpaceRegex().Replace(text, " ").Trim();
    }

    /// <summary>Collapses a run of adjacent (already-normalized) pause markers into the
    /// single longest one, so a paragraph pause meeting a model pause doesn't stack.</summary>
    private static string CollapsePauses(string run)
    {
        var max = 0;
        foreach (Match m in BracketTagRegex().Matches(run))
        {
            var value = m.Groups["tag"].Value.Trim();
            value = value["pause".Length..].TrimStart(':').Trim();
            value = value.EndsWith("ms", StringComparison.Ordinal) ? value[..^2] : value;
            if (int.TryParse(value, out var ms) && ms > max)
            {
                max = ms;
            }
        }

        return max > 0 ? $"[pause:{max}ms] " : string.Empty;
    }

    /// <summary>
    /// Removes every bracket marker entirely (no ellipsis substitution), leaving only the
    /// spoken words. Used to derive a clean transcript from a marked-up delivery.
    /// </summary>
    public static string StripMarkers(string markedUpText)
    {
        if (string.IsNullOrWhiteSpace(markedUpText))
        {
            return string.Empty;
        }

        var text = BracketTagRegex().Replace(markedUpText, string.Empty);
        return MultiSpaceRegex().Replace(text, " ").Trim();
    }

    private static string NormalizeTag(string tag)
    {
        var trimmed = tag.Trim().ToLowerInvariant();

        if (trimmed == "breath")
        {
            return "[breath]";
        }

        if (trimmed.StartsWith("pause", StringComparison.Ordinal))
        {
            var valuePart = trimmed["pause".Length..].TrimStart(':').Trim();
            valuePart = valuePart.EndsWith("ms", StringComparison.Ordinal) ? valuePart[..^2] : valuePart;
            if (int.TryParse(valuePart, out var ms))
            {
                var scaled = (int)Math.Round(ms * PauseScale);
                var clamped = Math.Clamp(scaled, MinPauseMs, MaxPauseMs);
                return $"[pause:{clamped}ms]";
            }

            return string.Empty; // malformed pause tag
        }

        if (trimmed.StartsWith("rate:", StringComparison.Ordinal))
        {
            var rate = trimmed["rate:".Length..].Trim();
            return ValidRates.Contains(rate) ? $"[rate:{rate}]" : string.Empty;
        }

        return string.Empty; // unknown tag
    }
}
