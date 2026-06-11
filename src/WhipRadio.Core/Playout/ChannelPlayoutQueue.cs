using WhipRadio.Core.Abstractions;

namespace WhipRadio.Core.Playout;

/// <summary>
/// Deque-backed playout queue; single consumer (the PlayoutService).
/// Normal items append FIFO; priority items (listener greetings, dedications)
/// jump to the front so they air right after the current item.
/// </summary>
public class ChannelPlayoutQueue : IPlayoutQueue
{
    private readonly LinkedList<PlayoutItem> _items = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public void Enqueue(PlayoutItem item)
    {
        lock (_lock)
        {
            _items.AddLast(item);
        }

        _available.Release();
    }

    public void EnqueueFront(PlayoutItem item)
    {
        lock (_lock)
        {
            _items.AddFirst(item);
        }

        _available.Release();
    }

    public PlayoutItem? PeekNext()
    {
        lock (_lock)
        {
            return _items.First?.Value;
        }
    }

    public async Task<PlayoutItem> DequeueAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct);
        lock (_lock)
        {
            var item = _items.First!.Value;
            _items.RemoveFirst();
            return item;
        }
    }
}
