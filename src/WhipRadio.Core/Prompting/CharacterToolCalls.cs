using System.Text.Json;
using WhipRadio.Core.Json;

namespace WhipRadio.Core.Prompting;

public sealed record CharacterToolCall(
    string Name,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record CharacterToolParseResult(
    bool IsValid,
    CharacterToolCall Call,
    string? Error = null);

public interface ICharacterToolCallParser
{
    CharacterToolParseResult Parse(string raw, IReadOnlyList<CharacterToolDefinition> allowedTools);
}

public sealed class CharacterToolCallParser : ICharacterToolCallParser
{
    private const string NoOp = "NoOp";

    public CharacterToolParseResult Parse(string raw, IReadOnlyList<CharacterToolDefinition> allowedTools)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Invalid("The model returned an empty tool call.");
        }

        try
        {
            using var doc = JsonDocument.Parse(StructuredJson.StripCodeFence(raw));
            var root = SelectToolObject(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The model did not return a JSON object tool call.");
            }

            var toolName = ReadToolName(root);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return Invalid("The tool call is missing a tool name.");
            }

            var definition = allowedTools.FirstOrDefault(tool =>
                string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return Invalid($"Tool '{toolName}' is not available in this prompt scope.");
            }

            var arguments = ReadArguments(root);
            foreach (var required in definition.Arguments.Where(argument => argument.IsRequired))
            {
                if (!arguments.TryGetValue(required.Name, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return Invalid($"Tool '{definition.Name}' is missing required argument '{required.Name}'.");
                }
            }

            return new CharacterToolParseResult(true, new CharacterToolCall(definition.Name, arguments));
        }
        catch (JsonException ex)
        {
            return Invalid($"The model did not return valid JSON: {ex.Message}");
        }
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

    private static string? ReadToolName(JsonElement root)
    {
        if (root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
        {
            return tool.GetString();
        }

        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadArguments(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in arguments.EnumerateObject())
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

    private static CharacterToolParseResult Invalid(string error)
        => new(
            false,
            new CharacterToolCall(NoOp, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reason"] = error,
            }),
            error);
}
