using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

/// <summary>One item the playout engine streams: a track or an announcement WAV.</summary>
public sealed record PlayoutItem(
    PlayoutItemType ItemType,
    Guid ItemId,
    string FilePath,
    string Title,
    double DurationSeconds,
    int? ModeratorId = null);

/// <summary>Thread-safe FIFO consumed by the PlayoutService.</summary>
public interface IPlayoutQueue
{
    void Enqueue(PlayoutItem item);

    Task<PlayoutItem> DequeueAsync(CancellationToken ct);

    int Count { get; }
}
