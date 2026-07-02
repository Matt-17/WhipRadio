using System.Text.Json;
using WhipRadio.Core.Json;
using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Prompting;

public sealed class ChatReplyParser : IChatReplyParser
{
    public ChatReply Parse(string raw, IReadOnlyList<CharacterToolDefinition> allowedTools)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ChatReply(string.Empty, [], ["The model returned an empty response."]);
        }

        string json = ExtractJsonObject(raw);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidJsonFallback(raw, "The model did not return a JSON object.");
            }

            string prose = ReadString(doc.RootElement, "reply") ?? string.Empty;
            List<CharacterToolCall> actions = [];
            List<string> errors = [];

            if (doc.RootElement.TryGetProperty("actions", out JsonElement actionItems)
                && actionItems.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement action in actionItems.EnumerateArray())
                {
                    CharacterToolCall? parsed = ParseAction(action, allowedTools, errors);
                    if (parsed is not null)
                    {
                        actions.Add(parsed);
                    }
                }
            }
            else
            {
                errors.Add("The chat reply is missing an actions array.");
            }

            if (string.IsNullOrWhiteSpace(prose) && actions.Count == 0 && errors.Count == 0)
            {
                errors.Add("The chat reply contained no prose and no actions.");
            }

            return new ChatReply(LlmOutputSanitizer.Sanitize(prose), actions, errors);
        }
        catch (JsonException ex)
        {
            return InvalidJsonFallback(raw, $"The model did not return valid JSON: {ex.Message}");
        }
    }

    private static ChatReply InvalidJsonFallback(string raw, string error)
        => new(LlmOutputSanitizer.Sanitize(raw), [], [error]);

    private static CharacterToolCall? ParseAction(
        JsonElement action,
        IReadOnlyList<CharacterToolDefinition> allowedTools,
        List<string> errors)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            errors.Add("One action was not a JSON object.");
            return null;
        }

        string? toolName = ReadString(action, "tool") ?? ReadString(action, "name");
        if (string.IsNullOrWhiteSpace(toolName))
        {
            errors.Add("One action is missing a tool name.");
            return null;
        }

        CharacterToolDefinition? definition = allowedTools.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            errors.Add($"Tool '{toolName}' is not available in this prompt scope.");
            return null;
        }

        Dictionary<string, string> arguments = ReadArguments(action);
        foreach (CharacterToolArgument required in definition.Arguments.Where(argument => argument.IsRequired))
        {
            if (!arguments.TryGetValue(required.Name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Tool '{definition.Name}' is missing required argument '{required.Name}'.");
                return null;
            }
        }

        return new CharacterToolCall(definition.Name, arguments);
    }

    private static Dictionary<string, string> ReadArguments(JsonElement action)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (!action.TryGetProperty("arguments", out JsonElement arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }

        return result;
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ExtractJsonObject(string raw)
    {
        string trimmed = StructuredJson.StripCodeFence(raw);
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }
}
