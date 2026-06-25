using System.Text.Json.Nodes;

namespace WhipRadio.Core.Prompting;

/// <summary>
/// Builds the JSON Schema handed to the model's structured-output channel when it is
/// choosing a character tool. The set of tools is dynamic (it depends on prompt scope
/// and role), so the <c>tool</c> enum is built per call from the allowed tools.
/// <c>arguments</c> stays a permissive string map — per-tool required-argument checks
/// run after parsing in <see cref="CharacterToolCallParser"/>.
/// </summary>
public static class CharacterToolSchema
{
    public static JsonNode Build(IReadOnlyList<CharacterToolDefinition> allowedTools)
    {
        var toolNames = new JsonArray();
        foreach (var tool in allowedTools)
        {
            toolNames.Add(tool.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = toolNames,
                },
                ["arguments"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                },
            },
            ["required"] = new JsonArray("tool", "arguments"),
        };
    }
}
