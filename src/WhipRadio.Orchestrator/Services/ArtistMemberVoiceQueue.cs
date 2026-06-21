namespace WhipRadio.Orchestrator.Services;

/// <summary>In-memory work queue for hidden artist-member voice bootstrap.</summary>
public sealed class ArtistMemberVoiceQueue
{
    private readonly object _lock = new();
    private readonly Queue<Guid> _queue = new();
    private readonly HashSet<Guid> _queued = [];

    public void Enqueue(Guid memberId)
    {
        lock (_lock)
        {
            if (_queued.Add(memberId))
            {
                _queue.Enqueue(memberId);
            }
        }
    }

    public void EnqueueMany(IEnumerable<Guid> memberIds)
    {
        foreach (var memberId in memberIds)
        {
            Enqueue(memberId);
        }
    }

    public void EnqueuePriority(Guid memberId)
    {
        lock (_lock)
        {
            if (!_queued.Add(memberId))
            {
                return;
            }

            var existing = _queue.ToArray();
            _queue.Clear();
            _queue.Enqueue(memberId);
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
            if (!_queue.TryDequeue(out var memberId))
            {
                return null;
            }

            _queued.Remove(memberId);
            return memberId;
        }
    }

    public IReadOnlyList<Guid> QueuedMemberIds()
    {
        lock (_lock)
        {
            return [.. _queue];
        }
    }
}
