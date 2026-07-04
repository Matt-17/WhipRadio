namespace WhipRadio.Core.Entities;

public enum ParticipantMemoryKind
{
    TalkSummary = 0,
    ArtistFact = 1,
    ChatSummary = 2,
}

/// <summary>
/// One retrievable memory slice for a conversation/chat participant (Phase 5
/// thin retrieval): a short text plus its embedding, keyed by the same
/// "host:{id}" / "member:{guid}" / "guest:{guid}" speaker keys the
/// conversation engine uses. Scored in-process with cosine similarity.
/// </summary>
public class ParticipantMemory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"host:{moderatorId}", "member:{artistMemberGuid}", or "guest:{guestGuid}".</summary>
    public string ParticipantKey { get; set; } = string.Empty;

    public ParticipantMemoryKind Kind { get; set; }

    /// <summary>The retrievable text (≤ ~800 chars).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Embedding of <see cref="Content"/> (Npgsql real[]).</summary>
    public float[] Embedding { get; set; } = [];

    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>Where the memory came from, e.g. "conversation:{segmentId}".</summary>
    public string? SourceRef { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
