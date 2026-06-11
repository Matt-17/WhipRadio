using WhipRadio.Core.Abstractions;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Snapshot of what's waiting in the playout queue (for "up next" UI).</summary>
public class QueueStateTracker
{
    private readonly Lock _lock = new();
    private readonly List<PlayoutItem> _items = [];

    public void Enqueued(PlayoutItem item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public void EnqueuedFront(PlayoutItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
        }
    }

    public void Started(Guid itemId)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.ItemId == itemId);
        }
    }

    public IReadOnlyList<PlayoutItem> Snapshot()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }
}

/// <summary>IPlayoutQueue decorator that keeps the QueueStateTracker in sync.</summary>
public class TrackedPlayoutQueue(IPlayoutQueue inner, QueueStateTracker tracker) : IPlayoutQueue
{
    public int Count => inner.Count;

    public void Enqueue(PlayoutItem item)
    {
        inner.Enqueue(item);
        tracker.Enqueued(item);
    }

    public void EnqueueFront(PlayoutItem item)
    {
        inner.EnqueueFront(item);
        tracker.EnqueuedFront(item);
    }

    public Task<PlayoutItem> DequeueAsync(CancellationToken ct) => inner.DequeueAsync(ct);
}
