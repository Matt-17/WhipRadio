using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ChatReplyParserTests
{
    private static readonly IReadOnlyList<CharacterToolDefinition> Tools =
    [
        new(
            "Message",
            "Send a message.",
            [
                new CharacterToolArgument("characterId", "Target."),
                new CharacterToolArgument("message", "Body."),
            ]),
        new(
            "SearchMusic",
            "Search music.",
            [
                new CharacterToolArgument("query", "Query."),
                new CharacterToolArgument("limit", "Limit.", IsRequired: false),
            ]),
    ];

    [TestMethod]
    public void Parse_ValidEnvelope_ReturnsProseAndActions()
    {
        var parser = new ChatReplyParser();

        ChatReply reply = parser.Parse(
            """
            {"reply":"I'll check that.","actions":[{"tool":"SearchMusic","arguments":{"query":"synthwave","limit":3}}]}
            """,
            Tools);

        Assert.Equal("I'll check that.", reply.Prose);
        Assert.Empty(reply.Errors);
        Assert.Equal(1, reply.Actions.Count);
        Assert.Equal("SearchMusic", reply.Actions[0].Name);
        Assert.Equal("3", reply.Actions[0].Arguments["limit"]);
    }

    [TestMethod]
    public void Parse_FencedJsonWithUnknownTool_KeepsValidActionsAndReportsError()
    {
        var parser = new ChatReplyParser();

        ChatReply reply = parser.Parse(
            """
            ```json
            {
              "reply": "Done.",
              "actions": [
                { "tool": "DeleteLibrary", "arguments": { "id": "all" } },
                { "tool": "Message", "arguments": { "characterId": "Admin", "message": "I will keep it tight." } }
              ]
            }
            ```
            """,
            Tools);

        Assert.Equal("Done.", reply.Prose);
        Assert.Equal(1, reply.Actions.Count);
        Assert.Equal("Message", reply.Actions[0].Name);
        Assert.Equal(1, reply.Errors.Count);
        Assert.Contains("not available", reply.Errors[0]);
    }

    [TestMethod]
    public void Parse_MissingRequiredArgument_DropsActionAndReportsError()
    {
        var parser = new ChatReplyParser();

        ChatReply reply = parser.Parse(
            """{"reply":"I'll send it.","actions":[{"tool":"Message","arguments":{"characterId":"Admin"}}]}""",
            Tools);

        Assert.Empty(reply.Actions);
        Assert.Equal(1, reply.Errors.Count);
        Assert.Contains("missing required argument", reply.Errors[0]);
    }

    [TestMethod]
    public void Parse_MissingActionsArray_KeepsProseAndReportsCorrectionError()
    {
        var parser = new ChatReplyParser();

        ChatReply reply = parser.Parse("""{"reply":"Plain answer only."}""", Tools);

        Assert.Equal("Plain answer only.", reply.Prose);
        Assert.Empty(reply.Actions);
        Assert.Equal(1, reply.Errors.Count);
        Assert.Contains("missing an actions array", reply.Errors[0]);
    }
}
