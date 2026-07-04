using WhipRadio.Core.Conversations;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

[TestClass]
public class AddressedToRoundRobinPolicyTests
{
    private static readonly List<ConversationParticipant> FiveSpeakers =
    [
        Participant("host:1", "Nova Quinn"),
        Participant("member:11111111-1111-1111-1111-111111111111", "Makoa Hale"),
        Participant("member:22222222-2222-2222-2222-222222222222", "Tessa Burdinsky"),
        Participant("guest:33333333-3333-3333-3333-333333333333", "Ivy Sparks"),
        Participant("host:2", "Rex Halloway"),
    ];

    private readonly AddressedToRoundRobinPolicy _policy = new();

    [TestMethod]
    public void FirstTurn_GoesToTheLead()
    {
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, [], null));
        Assert.Equal("host:1", next);
    }

    [TestMethod]
    public void ExplicitAddressedTo_Wins()
    {
        var turns = new List<ConversationTurn> { Turn("host:1", "Ivy, what do you think?") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(
            FiveSpeakers, turns, "guest:33333333-3333-3333-3333-333333333333"));
        Assert.Equal("guest:33333333-3333-3333-3333-333333333333", next);
    }

    [TestMethod]
    public void NameMentionInLastTurn_PicksThatSpeaker()
    {
        var turns = new List<ConversationTurn> { Turn("host:1", "Tessa Burdinsky, you were there, right?") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, null));
        Assert.Equal("member:22222222-2222-2222-2222-222222222222", next);
    }

    [TestMethod]
    public void MentioningYourOwnName_DoesNotKeepTheFloor()
    {
        var turns = new List<ConversationTurn> { Turn("host:1", "I'm Nova Quinn and this is my show.") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, null));
        Assert.NotEqual("host:1", next);
    }

    [TestMethod]
    public void RoundRobin_AdvancesFromLastSpeakerAndNeverRepeats()
    {
        var turns = new List<ConversationTurn> { Turn(FiveSpeakers[2].SpeakerKey, "No names here.") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, null));
        Assert.Equal(FiveSpeakers[3].SpeakerKey, next);

        turns.Add(Turn(next, "Still no names."));
        var afterThat = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, null));
        Assert.Equal(FiveSpeakers[4].SpeakerKey, afterThat);
    }

    [TestMethod]
    public void RoundRobin_WrapsAroundTheRoster()
    {
        var turns = new List<ConversationTurn> { Turn(FiveSpeakers[^1].SpeakerKey, "Plain turn.") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, null));
        Assert.Equal(FiveSpeakers[0].SpeakerKey, next);
    }

    [TestMethod]
    public void AddressedToLastSpeaker_FallsThroughToRoundRobin()
    {
        var turns = new List<ConversationTurn> { Turn("host:1", "Plain turn.") };
        var next = _policy.NextSpeakerKey(new TurnTakingState(FiveSpeakers, turns, "host:1"));
        Assert.NotEqual("host:1", next);
    }

    private static ConversationParticipant Participant(string key, string name)
        => new() { SpeakerKey = key, DisplayName = name, ConversationRole = "Guest" };

    private static ConversationTurn Turn(string key, string text)
        => new() { SpeakerKey = key, Text = text };
}
