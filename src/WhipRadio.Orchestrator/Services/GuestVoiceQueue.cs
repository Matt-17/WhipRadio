namespace WhipRadio.Orchestrator.Services;

/// <summary>In-memory work queue for guest voice bootstrap.</summary>
public sealed class GuestVoiceQueue
{
    private readonly object _lock = new();
    private readonly Queue<Guid> _queue = new();
    private readonly HashSet<Guid> _queued = [];

    public void Enqueue(Guid guestId)
    {
        lock (_lock)
        {
            if (_queued.Add(guestId))
            {
                _queue.Enqueue(guestId);
            }
        }
    }

    public void EnqueueMany(IEnumerable<Guid> guestIds)
    {
        foreach (var guestId in guestIds)
        {
            Enqueue(guestId);
        }
    }

    public void EnqueuePriority(Guid guestId)
    {
        lock (_lock)
        {
            if (!_queued.Add(guestId))
            {
                return;
            }

            var existing = _queue.ToArray();
            _queue.Clear();
            _queue.Enqueue(guestId);
            foreach (var queuedId in existing)
            {
                _queue.Enqueue(queuedId);
            }
        }
    }

    public Guid? TryDequeue()
    {
        lock (_lock)
        {
            if (!_queue.TryDequeue(out var guestId))
            {
                return null;
            }

            _queued.Remove(guestId);
            return guestId;
        }
    }

    public IReadOnlyList<Guid> QueuedGuestIds()
    {
        lock (_lock)
        {
            return [.. _queue];
        }
    }
}
