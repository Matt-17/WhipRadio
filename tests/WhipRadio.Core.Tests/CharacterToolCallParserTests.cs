using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class CharacterToolCallParserTests
{
    private static readonly IReadOnlyList<CharacterToolDefinition> Tools =
    [
        new(
            "Announce",
            "Create spoken text.",
            [new CharacterToolArgument("text", "Spoken text.")]),
        new(
            "NoOp",
            "Do nothing.",
            [new CharacterToolArgument("reason", "Reason.", IsRequired: false)]),
    ];

    [TestMethod]
    public void Parse_ValidJsonToolCall_ReturnsCall()
    {
        var parser = new CharacterToolCallParser();

        var result = parser.Parse(
            """{"tool":"Announce","arguments":{"text":"Good evening."}}""",
            Tools);

        Assert.True(result.IsValid);
        Assert.Equal("Announce", result.Call.Name);
        Assert.Equal("Good evening.", result.Call.Arguments["text"]);
    }

    [TestMethod]
    public void Parse_FencedJson_ReturnsCall()
    {
        var parser = new CharacterToolCallParser();

        var result = parser.Parse(
            """
            ```json
            {"tool":"Announce","arguments":{"text":"Weather next."}}
            ```
            """,
            Tools);

        Assert.True(result.IsValid);
        Assert.Equal("Weather next.", result.Call.Arguments["text"]);
    }

    [TestMethod]
    public void Parse_MissingRequiredArgument_ReturnsNoOpFallback()
    {
        var parser = new CharacterToolCallParser();

        var result = parser.Parse("""{"tool":"Announce","arguments":{}}""", Tools);

        Assert.False(result.IsValid);
        Assert.Equal("NoOp", result.Call.Name);
        Assert.Contains("missing required argument", result.Call.Arguments["reason"]);
    }

    [TestMethod]
    public void Parse_UnsupportedTool_ReturnsNoOpFallback()
    {
        var parser = new CharacterToolCallParser();

        var result = parser.Parse(
            """{"tool":"DeleteLibrary","arguments":{"id":"all"}}""",
            Tools);

        Assert.False(result.IsValid);
        Assert.Equal("NoOp", result.Call.Name);
        Assert.Contains("not available", result.Call.Arguments["reason"]);
    }
}
