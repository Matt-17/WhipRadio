using WhipRadio.Core.Conversations;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ConversationBudgetTests
{
    [TestMethod]
    public void WordBudget_ScalesWithMinutesAt150Wpm()
    {
        Assert.Equal(1500, ConversationBudget.WordBudget(10));
        Assert.Equal(150, ConversationBudget.WordBudget(0)); // clamped to one minute
    }

    [TestMethod]
    public void TurnCap_ComesFromWordBudgetWithFloorAndCeiling()
    {
        // 15 min * 150 wpm / 45 words per turn = 50 → capped at 40.
        Assert.Equal(40, ConversationBudget.TurnCap(15, 2));
        // 10 min → 1500/45 = 33 turns.
        Assert.Equal(33, ConversationBudget.TurnCap(10, 2));
        // Tiny budget never goes below two turns per participant.
        Assert.Equal(10, ConversationBudget.TurnCap(1, 5));
    }

    [TestMethod]
    public void MaxLlmCalls_IsTurnCapPlusPlanAndStaysUnderTheCeiling()
    {
        Assert.Equal(34, ConversationBudget.MaxLlmCalls(10, 2));
        // Longest episode: turn cap 40 + 1 plan call, comfortably under the hard ceiling.
        Assert.Equal(41, ConversationBudget.MaxLlmCalls(30, 2));
        Assert.True(ConversationBudget.MaxLlmCalls(30, 5) <= ConversationBudget.MaxLlmCallCeiling);
    }
}
