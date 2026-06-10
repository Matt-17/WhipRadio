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

    [GeneratedRegex(@"\[(?<tag>[^\[\]]*)\]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"\[breath\](?:\s*\[breath\])+")]
    private static partial Regex DuplicateBreathRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    private static readonly string[] ValidRates = ["slow", "normal", "fast"];

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = BracketTagRegex().Replace(text, match => NormalizeTag(match.Groups["tag"].Value));
        normalized = DuplicateBreathRegex().Replace(normalized, "[breath]");
        normalized = MultiSpaceRegex().Replace(normalized, " ");
        return normalized.Trim();
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
                var clamped = Math.Clamp(ms, MinPauseMs, MaxPauseMs);
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
