using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ChatActionPolicyTests
{
    [TestMethod]
    public void IsInTurnLookup_RecognizesSearchAndStatusTools()
    {
        Assert.True(ChatActionPolicy.IsInTurnLookup(Call("SearchMusic", EmptyArguments())));
        Assert.True(ChatActionPolicy.IsInTurnLookup(Call("StatusReport", EmptyArguments())));
        Assert.False(ChatActionPolicy.IsInTurnLookup(Call("Message", EmptyArguments())));
    }

    [TestMethod]
    public void IsTerminalAdminReport_RecognizesAdminMessage()
    {
        CharacterToolCall call = Call("Message", new Dictionary<string, string>
        {
            ["characterId"] = "Admin",
            ["message"] = "Jenny and I planned a short synthwave segment.",
        });

        Assert.True(ChatActionPolicy.IsTerminalAdminReport(call));
        Assert.False(ChatActionPolicy.WouldEnqueueAgentTurn(call));
    }

    [TestMethod]
    public void WouldEnqueueAgentTurn_RecognizesHostOrDirectorMessage()
    {
        CharacterToolCall hostMessage = Call("Message", new Dictionary<string, string>
        {
            ["characterId"] = "Jenny",
            ["message"] = "Want to plan the segment?",
        });

        Assert.True(ChatActionPolicy.WouldEnqueueAgentTurn(hostMessage));
        Assert.False(ChatActionPolicy.IsTerminalAdminReport(hostMessage));
    }

    private static CharacterToolCall Call(string name, IReadOnlyDictionary<string, string> arguments)
        => new(name, new Dictionary<string, string>(arguments, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> EmptyArguments()
        => new Dictionary<string, string>();
}
