using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ChannelPlayoutQueueTests
{
    private static PlayoutItem Item(string title)
        => new(PlayoutItemType.Track, Guid.NewGuid(), $"library/tracks/{title}.wav", title, 90);

    [TestMethod]
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

    [TestMethod]
    public async Task DequeueAsync_WaitsForItem()
    {
        var queue = new ChannelPlayoutQueue();
        var pending = queue.DequeueAsync(CancellationToken.None);

        Assert.False(pending.IsCompleted);

        var item = Item("late");
        queue.Enqueue(item);

        Assert.Equal(item, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task EnqueueFront_JumpsTheLine()
    {
        var queue = new ChannelPlayoutQueue();
        var track = Item("track");
        var nextTrack = Item("next-track");
        var greeting = Item("greeting");

        queue.Enqueue(track);
        queue.Enqueue(nextTrack);
        queue.EnqueueFront(greeting);

        Assert.Equal(3, queue.Count);
        Assert.Equal(greeting, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(track, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(nextTrack, await queue.DequeueAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task EnqueueFront_TalkThenTrackStayAdjacent()
    {
        // Dedication pattern: talk enqueued normally, then its track — FIFO keeps them paired.
        var queue = new ChannelPlayoutQueue();
        var talk = Item("dedication-talk");
        var requested = Item("requested-track");

        queue.Enqueue(talk);
        queue.Enqueue(requested);

        Assert.Equal(talk, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(requested, await queue.DequeueAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task DequeueAsync_CancellationThrows()
    {
        var queue = new ChannelPlayoutQueue();
        using var cts = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
