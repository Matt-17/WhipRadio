using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Conversations;

/// <summary>Everything a policy may look at when picking the next speaker.</summary>
public sealed record TurnTakingState(
    IReadOnlyList<ConversationParticipant> Participants,
    IReadOnlyList<ConversationTurn> Turns,
    string? LastAddressedToKey);

/// <summary>
/// Decides whose turn it is in a multi-agent conversation (Phase 5).
/// Pluggable so the policy can evolve from round-robin toward a social model
/// without touching the director.
/// </summary>
public interface ITurnTakingPolicy
{
    /// <summary>Returns the SpeakerKey of the participant who speaks next.</summary>
    string NextSpeakerKey(TurnTakingState state);
}
