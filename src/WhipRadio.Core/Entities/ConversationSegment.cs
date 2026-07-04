namespace WhipRadio.Core.Entities;

public enum ConversationKind
{
    /// <summary>A tight two-way exchange (interview, quick studio talk).</summary>
    Talk,

    /// <summary>A longer host-led episode, usually chaptered.</summary>
    Podcast,
}

public enum ConversationStructure
{
    Freeform,
    Chaptered,
}

public enum ConversationStatus
{
    Planned,
    Scripted,
    Produced,
    Queued,
    Used,
    Failed,
}

/// <summary>
/// A produce-ahead multi-speaker talk or podcast episode (Phase 3c.2): one
/// speaker-tagged script, each turn voiced with its speaker's own designed
/// voice, assembled into one WAV. Participants, chapters, and turns persist as
/// JSON columns — the turn schema is generation-agnostic, so a later
/// multi-agent writer emits the same records without a schema change.
/// </summary>
public class ConversationSegment
{
    public Guid Id { get; set; }

    public ConversationKind Kind { get; set; }

    public ConversationStructure Structure { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string Brief { get; set; } = string.Empty;

    public int TargetDurationMinutes { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Planned;

    /// <summary>JSON <c>List&lt;ConversationParticipant&gt;</c> — ordered, the first entry leads.</summary>
    public string ParticipantsJson { get; set; } = "[]";

    /// <summary>JSON <c>List&lt;ConversationChapter&gt;</c>; empty for Freeform.</summary>
    public string ChaptersJson { get; set; } = "[]";

    /// <summary>JSON <c>List&lt;ConversationTurn&gt;</c>, written when the script lands.</summary>
    public string? TurnsJson { get; set; }

    /// <summary>Episode title invented by the writer.</summary>
    public string? Title { get; set; }

    /// <summary>Readable "Name: text" transcript of the whole conversation.</summary>
    public string? Transcript { get; set; }

    /// <summary>Relative to the /data root: <c>library/conversations/{id}.wav</c>.</summary>
    public string? OutputFilePath { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>The ScheduledOnly playout wrapper created at Produced.</summary>
    public Guid? AnnouncementId { get; set; }

    /// <summary>Set for podcast-show episodes; null for one-off ad-hoc conversations.</summary>
    public Guid? PodcastShowId { get; set; }

    /// <summary>Slot occurrence this episode fills; null for one-off conversations.</summary>
    public DateTime? TargetUtc { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Human-readable "what's happening now" production label.</summary>
    public string? ProductionState { get; set; }

    /// <summary>Current production step (1-based); 0 when idle.</summary>
    public int StepIndex { get; set; }

    public int StepTotal { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ProducedAtUtc { get; set; }

    public DateTime? UsedAtUtc { get; set; }
}

/// <summary>One ordered speaker in a conversation, serialized into ParticipantsJson.</summary>
public class ConversationParticipant
{
    public const string HostKeyPrefix = "host:";
    public const string MemberKeyPrefix = "member:";

    /// <summary>"host:{moderatorId}" or "member:{artistMemberGuid}".</summary>
    public string SpeakerKey { get; set; } = string.Empty;

    /// <summary>Display name snapshot taken at creation time.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Host", "Cohost", or "Guest".</summary>
    public string ConversationRole { get; set; } = "Host";

    public static string HostKey(int moderatorId) => $"{HostKeyPrefix}{moderatorId}";

    public static string MemberKey(Guid artistMemberId) => $"{MemberKeyPrefix}{artistMemberId:D}";

    public bool TryGetModeratorId(out int moderatorId)
    {
        moderatorId = 0;
        return SpeakerKey.StartsWith(HostKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(SpeakerKey.AsSpan(HostKeyPrefix.Length), out moderatorId);
    }

    public bool TryGetArtistMemberId(out Guid artistMemberId)
    {
        artistMemberId = Guid.Empty;
        return SpeakerKey.StartsWith(MemberKeyPrefix, StringComparison.Ordinal)
            && Guid.TryParse(SpeakerKey.AsSpan(MemberKeyPrefix.Length), out artistMemberId);
    }
}

/// <summary>An optional chapter outline entry, serialized into ChaptersJson.</summary>
public class ConversationChapter
{
    public string Title { get; set; } = string.Empty;

    public string Intent { get; set; } = string.Empty;

    public int TargetMinutes { get; set; }
}

/// <summary>
/// One spoken turn, serialized into TurnsJson. Deliberately generation-agnostic:
/// today one LLM call emits the whole list; a later multi-agent engine appends
/// the same records one utterance at a time.
/// </summary>
public class ConversationTurn
{
    public string SpeakerKey { get; set; } = string.Empty;

    /// <summary>Clean spoken text (transcript form).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Optional marked-up variant with [pause:NNNms]/[breath] speech markers.</summary>
    public string? Markers { get; set; }

    /// <summary>Timing hint: silence after this turn; null = default gap.</summary>
    public int? PauseAfterMs { get; set; }
}
