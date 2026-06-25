namespace WhipRadio.Orchestrator.Services;

/// <summary>In-memory work queue for background host (moderator) voice design.</summary>
public sealed class HostVoiceQueue
{
    private readonly object _lock = new();
    private readonly Queue<int> _queue = new();
    private readonly HashSet<int> _queued = [];

    public void Enqueue(int moderatorId)
    {
        lock (_lock)
        {
            if (_queued.Add(moderatorId))
            {
                _queue.Enqueue(moderatorId);
            }
        }
    }

    public void EnqueueMany(IEnumerable<int> moderatorIds)
    {
        foreach (var moderatorId in moderatorIds)
        {
            Enqueue(moderatorId);
        }
    }

    public int? TryDequeue()
    {
        lock (_lock)
        {
            if (!_queue.TryDequeue(out var moderatorId))
            {
                return null;
            }

            _queued.Remove(moderatorId);
            return moderatorId;
        }
    }
}
