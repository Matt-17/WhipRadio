using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

/// <summary>One item the playout engine streams: a track or an announcement WAV.</summary>
/// <remarks>
/// <see cref="IsResumed"/> marks an item rehydrated from persisted playout state
/// after a process restart. Such an item already aired (and was logged) before the
/// restart, so the play log must not record it a second time — see PlaybackReporter.
/// </remarks>
public sealed record PlayoutItem(
    PlayoutItemType ItemType,
    Guid ItemId,
    string FilePath,
    string Title,
    double DurationSeconds,
    int? ModeratorId = null,
    double StartOffsetSeconds = 0,
    bool IsResumed = false);

/// <summary>Thread-safe FIFO consumed by the PlayoutService.</summary>
public interface IPlayoutQueue
{
    void Enqueue(PlayoutItem item);

    /// <summary>Jumps the line: the item plays right after the current one
    /// (priority talk like listener greetings).</summary>
    void EnqueueFront(PlayoutItem item);

    /// <summary>Next item without removing it — mixer lookahead/prefetch.</summary>
    PlayoutItem? PeekNext();

    Task<PlayoutItem> DequeueAsync(CancellationToken ct);

    int Count { get; }
}
