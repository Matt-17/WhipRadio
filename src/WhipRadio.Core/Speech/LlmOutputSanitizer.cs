using System.Text.RegularExpressions;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Defensive cleanup of LLM output that ignored prompt instructions:
/// strips code fences, surrounding quotes and leading "Sure, here is..." filler lines.
/// </summary>
public static partial class LlmOutputSanitizer
{
    [GeneratedRegex(@"^(sure|certainly|of course|okay|ok|here('|’)s|here is|hier ist|gerne|klar|natürlich)\b.{0,80}?:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadInRegex();

    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text.Trim();

        // Strip markdown code fences (``` or ```lang ... ```).
        if (result.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = result.IndexOf('\n');
            if (firstNewline >= 0)
            {
                result = result[(firstNewline + 1)..];
            }
            var fenceEnd = result.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                result = result[..fenceEnd];
            }
            result = result.Trim();
        }

        result = result.Replace("`", string.Empty);

        // Strip a leading conversational lead-in line ("Sure, here is your intro:").
        result = LeadInRegex().Replace(result, string.Empty).Trim();

        // Strip one pair of symmetric surrounding quotes.
        result = StripSurroundingQuotes(result);

        return result.Trim();
    }

    private static string StripSurroundingQuotes(string text)
    {
        (char Open, char Close)[] pairs = [('"', '"'), ('“', '”'), ('„', '“'), ('\'', '\''), ('«', '»')];
        foreach (var (open, close) in pairs)
        {
            if (text.Length >= 2 && text[0] == open && text[^1] == close)
            {
                return text[1..^1].Trim();
            }
        }

        return text;
    }
}
