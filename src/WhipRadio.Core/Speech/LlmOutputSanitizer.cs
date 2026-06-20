using System.Text.RegularExpressions;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Defensive cleanup of LLM output that ignored prompt instructions: strips code
/// fences, markdown emphasis, surrounding quotes, parenthetical stage directions,
/// and — critically — the model's meta-chatter about its own task ("Okay, here we
/// go:", "I created a text for a song intro:", "Let me know if …"). Only the
/// words meant to be SPOKEN may reach the TTS.
/// </summary>
public static partial class LlmOutputSanitizer
{
    [GeneratedRegex(@"^(sure|certainly|of course|okay|ok|alright|here('|’)s|here is|here we go|i('|’)ve|i have|i('|’)ll|i will|i create[d]?|i wrote|let me)\b.{0,100}?:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadInWithColonRegex();

    [GeneratedRegex(@"^(okay|ok|alright|sure|here('|’)s|here is|here we go|i('|’)ve|i have|i('|’)ll|i will|i create[d]?|i wrote|let me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetaOpenerRegex();

    [GeneratedRegex(@"\b(text|script|intro|outro|announcement|moderation|version|copy|for you)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetaVocabularyRegex();

    [GeneratedRegex(@"\b(let me know|hope (this|that|you)|feel free)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingMetaRegex();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

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

        // Markdown leftovers have no spoken form.
        result = result.Replace("`", string.Empty).Replace("*", string.Empty).Replace("#", string.Empty);

        // Strip surrounding quotes both before and after the lead-in removal:
        // quotes may wrap the whole reply including the lead-in, or just the copy itself.
        result = StripSurroundingQuotes(result);

        // "Okay, here we go: <copy>" — same-line meta lead-in up to the colon.
        result = LeadInWithColonRegex().Replace(result, string.Empty).Trim();

        // "I created a text for a song intro:\n<copy>" — whole meta first line.
        result = StripMetaFirstLine(result);

        // "…\nLet me know if you want changes!" — trailing meta line.
        result = StripTrailingMetaLine(result);

        // Parentheses in radio copy are stage directions ("(Sound of a synth)") —
        // they have no spoken equivalent, so drop them.
        result = ParentheticalRegex().Replace(result, " ");
        result = MultiSpaceRegex().Replace(result, " ");
        result = result.Replace(" .", ".").Replace(" ,", ",");

        result = StripSurroundingQuotes(result);

        return result.Trim();
    }

    /// <summary>Drops a first line that is clearly the model talking about its task,
    /// but only when real copy remains afterwards.</summary>
    private static string StripMetaFirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        if (newlineIndex < 0)
        {
            return text;
        }

        var firstLine = text[..newlineIndex].Trim();
        var rest = text[(newlineIndex + 1)..].Trim();
        if (rest.Length == 0)
        {
            return text;
        }

        var looksMeta = MetaOpenerRegex().IsMatch(firstLine)
            && (firstLine.EndsWith(':') || MetaVocabularyRegex().IsMatch(firstLine));
        return looksMeta ? rest : text;
    }

    private static string StripTrailingMetaLine(string text)
    {
        var newlineIndex = text.LastIndexOf('\n');
        if (newlineIndex < 0)
        {
            return text;
        }

        var lastLine = text[(newlineIndex + 1)..].Trim();
        var rest = text[..newlineIndex].Trim();
        if (rest.Length == 0)
        {
            return text;
        }

        return TrailingMetaRegex().IsMatch(lastLine) ? rest : text;
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
