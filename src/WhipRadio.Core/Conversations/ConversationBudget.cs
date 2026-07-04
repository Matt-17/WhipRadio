namespace WhipRadio.Core.Conversations;

/// <summary>
/// Pure budget math for multi-agent conversations: N participants × many turns
/// is many LLM calls, so the turn count is capped from the 150-wpm word budget
/// before the first call is made.
/// </summary>
public static class ConversationBudget
{
    public const int WordsPerMinute = 150;

    /// <summary>Typical conversational turn length used for capping.</summary>
    public const int AverageWordsPerTurn = 45;

    /// <summary>Hard ceiling on LLM calls per episode regardless of duration.</summary>
    public const int MaxLlmCallCeiling = 48;

    public static int WordBudget(int targetMinutes)
        => Math.Max(1, targetMinutes) * WordsPerMinute;

    /// <summary>Turn cap from the word budget, clamped to [2 per participant, 40].</summary>
    public static int TurnCap(int targetMinutes, int participantCount)
    {
        var byWords = WordBudget(targetMinutes) / AverageWordsPerTurn;
        var floor = Math.Max(2, participantCount) * 2;
        return Math.Clamp(byWords, floor, 40);
    }

    /// <summary>Estimated LLM calls: one per turn plus the episode-plan call.</summary>
    public static int MaxLlmCalls(int targetMinutes, int participantCount)
        => Math.Min(TurnCap(targetMinutes, participantCount) + 1, MaxLlmCallCeiling);
}
