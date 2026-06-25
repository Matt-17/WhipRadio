using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class LocalGpuSchedulerTests
{
    private const string Group = "gpu:test";

    private static LocalGpuScheduler NewScheduler() => new(NullLogger<LocalGpuScheduler>.Instance);

    private static async Task<LocalGpuScheduler.GpuLease> AwaitGrant(Task<LocalGpuScheduler.GpuLease> task)
        => await task.WaitAsync(TimeSpan.FromSeconds(5));

    [TestMethod]
    public async Task OneHolderAtATime()
    {
        var scheduler = NewScheduler();
        var first = await AwaitGrant(scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default));

        var second = scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        var lease = await AwaitGrant(second);
        Assert.NotNull(lease);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task HighestPriorityWaiterWinsFirst()
    {
        var scheduler = NewScheduler();
        var holder = await AwaitGrant(scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default));

        var low = scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Low, default);
        var high = scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.High, default);
        var normal = scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default);

        await holder.DisposeAsync();

        var winner = await Task.WhenAny(low, high, normal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(winner, high), "highest-priority waiter should be admitted first");
        Assert.False(low.IsCompleted);
        Assert.False(normal.IsCompleted);
    }

    [TestMethod]
    public async Task AffinityBreaksTieOverFifo()
    {
        var scheduler = NewScheduler();
        // Run + release a "writer" job so the resident model is "writer".
        var holder = await AwaitGrant(scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default));

        // Enqueue the non-matching waiter FIRST so FIFO alone would pick it.
        var voice = scheduler.AcquireAsync(Group, "voice", () => GpuJobPriority.Normal, default);
        var writer = scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default);

        await holder.DisposeAsync(); // LoadedAffinity becomes "writer"

        var winner = await Task.WhenAny(voice, writer).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(winner, writer), "same-priority waiter matching the resident model should win");
        Assert.False(voice.IsCompleted);
    }

    [TestMethod]
    public async Task FifoBreaksRemainingTie()
    {
        var scheduler = NewScheduler();
        var holder = await AwaitGrant(scheduler.AcquireAsync(Group, "x", () => GpuJobPriority.Normal, default));

        var firstIn = scheduler.AcquireAsync(Group, "y", () => GpuJobPriority.Normal, default);
        var secondIn = scheduler.AcquireAsync(Group, "y", () => GpuJobPriority.Normal, default);

        await holder.DisposeAsync();

        var winner = await Task.WhenAny(firstIn, secondIn).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(winner, firstIn), "with equal priority and affinity, the earlier waiter wins");
        Assert.False(secondIn.IsCompleted);
    }

    [TestMethod]
    public async Task DynamicPriorityIsReevaluatedAtSelection()
    {
        var scheduler = NewScheduler();
        var holder = await AwaitGrant(scheduler.AcquireAsync(Group, "x", () => GpuJobPriority.Normal, default));

        var ramp = new[] { GpuJobPriority.Low };
        var ramping = scheduler.AcquireAsync(Group, "a", () => ramp[0], default);
        var steady = scheduler.AcquireAsync(Group, "b", () => GpuJobPriority.Normal, default);

        // The ramping job overtakes once its priority rises above the steady one.
        ramp[0] = GpuJobPriority.High;
        await holder.DisposeAsync();

        var winner = await Task.WhenAny(ramping, steady).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(winner, ramping), "a job whose priority rose should be picked over a steady one");
        Assert.False(steady.IsCompleted);
    }

    [TestMethod]
    public async Task CancellationRemovesWaiter()
    {
        var scheduler = NewScheduler();
        var holder = await AwaitGrant(scheduler.AcquireAsync(Group, "x", () => GpuJobPriority.Normal, default));

        using var cts = new CancellationTokenSource();
        var waiter = scheduler.AcquireAsync(Group, "x", () => GpuJobPriority.Normal, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);

        // The holder can still release and the cancelled waiter does not deadlock the group.
        await holder.DisposeAsync();
        var next = await AwaitGrant(scheduler.AcquireAsync(Group, "x", () => GpuJobPriority.Normal, default));
        await next.DisposeAsync();
    }

    [TestMethod]
    public async Task PreviousAffinityTracksResidentModel()
    {
        var scheduler = NewScheduler();

        var first = await AwaitGrant(scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default));
        Assert.Null(first.PreviousAffinity);
        Assert.True(first.ModelSwitch);
        await first.DisposeAsync();

        var sameModel = await AwaitGrant(scheduler.AcquireAsync(Group, "writer", () => GpuJobPriority.Normal, default));
        Assert.Equal("writer", sameModel.PreviousAffinity);
        Assert.False(sameModel.ModelSwitch, "a consecutive same-model job must not switch (no reload)");
        await sameModel.DisposeAsync();

        var otherModel = await AwaitGrant(scheduler.AcquireAsync(Group, "voice", () => GpuJobPriority.Normal, default));
        Assert.Equal("writer", otherModel.PreviousAffinity);
        Assert.True(otherModel.ModelSwitch, "switching engines must report a model switch");
        await otherModel.DisposeAsync();
    }
}
