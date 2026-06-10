using System.Threading.Channels;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Core.Playout;

/// <summary>Unbounded channel-backed FIFO; single consumer (the PlayoutService).</summary>
public class ChannelPlayoutQueue : IPlayoutQueue
{
    private readonly Channel<PlayoutItem> _channel = Channel.CreateUnbounded<PlayoutItem>();
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Enqueue(PlayoutItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _count);
        }
    }

    public async Task<PlayoutItem> DequeueAsync(CancellationToken ct)
    {
        var item = await _channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref _count);
        return item;
    }
}
