using System.Text.Json;
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
        => NormalizePlainText(text);

    public static bool TrySanitizeSpokenText(string text, out string sanitized, out string? error)
    {
        sanitized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var candidate = StripCodeFence(text).Trim();
        if (!LooksLikeJson(candidate))
        {
            sanitized = NormalizePlainText(candidate);
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(candidate);
            var root = SelectToolObject(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The model returned JSON that is not a character tool object.";
                return false;
            }

            var toolName = ReadString(root, "tool") ?? ReadString(root, "name");
            if (!string.Equals(toolName, "Announce", StringComparison.OrdinalIgnoreCase))
            {
                error = string.IsNullOrWhiteSpace(toolName)
                    ? "The model returned JSON without an Announce tool name."
                    : $"The model returned unsupported tool JSON '{toolName}'.";
                return false;
            }

            if (!root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty("text", out var textArgument)
                || textArgument.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(textArgument.GetString()))
            {
                error = "The Announce tool JSON is missing required arguments.text.";
                return false;
            }

            sanitized = NormalizePlainText(textArgument.GetString()!);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"The model returned invalid JSON instead of spoken text: {ex.Message}";
            return false;
        }
    }

    private static string NormalizePlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = StripCodeFence(text).Trim();

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

    private static string StripCodeFence(string raw)
    {
        var result = raw.Trim();
        if (!result.StartsWith("```", StringComparison.Ordinal))
        {
            return result;
        }

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

        return result.Trim();
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            return true;
        }

        if (!trimmed.StartsWith('['))
        {
            return false;
        }

        var nextValueIndex = 1;
        while (nextValueIndex < trimmed.Length && char.IsWhiteSpace(trimmed[nextValueIndex]))
        {
            nextValueIndex++;
        }

        return nextValueIndex < trimmed.Length
            && (trimmed[nextValueIndex] == '{' || trimmed[nextValueIndex] == '[');
    }

    private static JsonElement SelectToolObject(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("tool_calls", out var calls)
            && calls.ValueKind == JsonValueKind.Array
            && calls.GetArrayLength() > 0)
        {
            return calls[0];
        }

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            return root[0];
        }

        return root;
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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
