using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class CharacterToolSchemaTests
{
    private static readonly IReadOnlyList<CharacterToolDefinition> Tools =
    [
        new("Announce", "Create spoken text.", [new CharacterToolArgument("text", "Spoken text.")]),
        new("NoOp", "Do nothing.", [new CharacterToolArgument("reason", "Reason.", IsRequired: false)]),
    ];

    [TestMethod]
    public void Build_ToolEnumReflectsAllowedTools()
    {
        var schema = CharacterToolSchema.Build(Tools);

        var toolEnum = schema!["properties"]!["tool"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToList();

        Assert.Contains("Announce", toolEnum);
        Assert.Contains("NoOp", toolEnum);
        Assert.Equal(2, toolEnum.Count);
    }

    [TestMethod]
    public void Build_RequiresToolAndArguments()
    {
        var schema = CharacterToolSchema.Build(Tools);

        var required = schema!["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        Assert.Contains("tool", required);
        Assert.Contains("arguments", required);
    }
}
