using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

public class ChannelPlayoutQueueTests
{
    private static PlayoutItem Item(string title)
        => new(PlayoutItemType.Track, Guid.NewGuid(), $"library/tracks/{title}.wav", title, 90);

    [Fact]
    public async Task DequeueAsync_ReturnsItemsInFifoOrder()
    {
        var queue = new ChannelPlayoutQueue();
        var first = Item("first");
        var second = Item("second");

        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert.Equal(2, queue.Count);
        Assert.Equal(first, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(second, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task DequeueAsync_WaitsForItem()
    {
        var queue = new ChannelPlayoutQueue();
        var pending = queue.DequeueAsync(CancellationToken.None);

        Assert.False(pending.IsCompleted);

        var item = Item("late");
        queue.Enqueue(item);

        Assert.Equal(item, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DequeueAsync_CancellationThrows()
    {
        var queue = new ChannelPlayoutQueue();
        using var cts = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
