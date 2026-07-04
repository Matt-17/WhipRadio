using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Conversations;

/// <summary>
/// Default turn-taking policy: an explicit addressed-to wins, then a name
/// mention in the last turn's text, then round-robin in roster order — never
/// handing the floor back to whoever just spoke. The first turn goes to the
/// lead (the roster's first participant).
/// </summary>
public sealed class AddressedToRoundRobinPolicy : ITurnTakingPolicy
{
    public string NextSpeakerKey(TurnTakingState state)
    {
        if (state.Participants.Count == 0)
        {
            throw new InvalidOperationException("A conversation needs participants.");
        }

        if (state.Turns.Count == 0)
        {
            return state.Participants[0].SpeakerKey;
        }

        var lastSpeakerKey = state.Turns[^1].SpeakerKey;

        // 1. The agent said who it was talking to.
        if (state.LastAddressedToKey is { } addressed
            && addressed != lastSpeakerKey
            && state.Participants.Any(participant => participant.SpeakerKey == addressed))
        {
            return addressed;
        }

        // 2. The last turn mentions another participant by name.
        var lastText = state.Turns[^1].Text;
        foreach (var participant in state.Participants)
        {
            if (participant.SpeakerKey != lastSpeakerKey
                && !string.IsNullOrWhiteSpace(participant.DisplayName)
                && MentionsName(lastText, participant.DisplayName))
            {
                return participant.SpeakerKey;
            }
        }

        // 3. Round-robin from the last speaker, skipping them.
        var lastIndex = IndexOf(state.Participants, lastSpeakerKey);
        for (var offset = 1; offset <= state.Participants.Count; offset++)
        {
            var candidate = state.Participants[(lastIndex + offset) % state.Participants.Count];
            if (candidate.SpeakerKey != lastSpeakerKey)
            {
                return candidate.SpeakerKey;
            }
        }

        return state.Participants[0].SpeakerKey;
    }

    private static int IndexOf(IReadOnlyList<ConversationParticipant> participants, string speakerKey)
    {
        for (var i = 0; i < participants.Count; i++)
        {
            if (participants[i].SpeakerKey == speakerKey)
            {
                return i;
            }
        }

        return 0;
    }

    private static bool MentionsName(string text, string name)
    {
        var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + name.Length;
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = text.IndexOf(name, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
