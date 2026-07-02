using System.Text.Json.Nodes;

namespace WhipRadio.Core.Prompting;

public static class ChatReplySchema
{
    public static JsonNode Build(IReadOnlyList<CharacterToolDefinition> allowedTools)
    {
        JsonArray toolNames = [];
        foreach (CharacterToolDefinition tool in allowedTools)
        {
            toolNames.Add(tool.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reply"] = new JsonObject
                {
                    ["type"] = "string",
                },
                ["actions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
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
                    },
                },
            },
            ["required"] = new JsonArray("reply", "actions"),
        };
    }
}
