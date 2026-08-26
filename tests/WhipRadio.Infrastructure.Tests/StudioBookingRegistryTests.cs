using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tests;

/// <summary>
/// Direct unit tests for the in-memory booking registry and the pending-operations
/// tracker extracted from StudioCoordinator. No database, no HTTP.
/// </summary>
[TestClass]
public class StudioBookingRegistryTests
{
    private static StudioJob Job(string label, string? group = null)
        => new(label, DateTime.UtcNow, GpuResourceGroup: group);

    [TestMethod]
    public void TryBook_RefusesDoubleBooking_AndGpuGroupContention()
    {
        var registry = new StudioBookingRegistry();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();
        var studioC = Guid.NewGuid();

        Assert.True(registry.TryBook(studioA, "gpu:local", Job("job A", "gpu:local")));
        Assert.False(registry.TryBook(studioA, "gpu:local", Job("again")));
        Assert.False(registry.TryBook(studioB, "gpu:local", Job("job B", "gpu:local")));
        Assert.True(registry.TryBook(studioC, "gpu:remote", Job("job C", "gpu:remote")));
        Assert.Equal(2, registry.ActiveJobs.Count);
    }

    [TestMethod]
    public void Release_FreesBookingAndGpuLease()
    {
        var registry = new StudioBookingRegistry();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();

        Assert.True(registry.TryBook(studioA, "gpu:local", Job("job A", "gpu:local")));
        registry.Release(studioA);

        Assert.Empty(registry.ActiveJobs);
        Assert.True(registry.TryBook(studioB, "gpu:local", Job("job B", "gpu:local")));
    }

    [TestMethod]
    public void Release_OfUnknownStudio_IsANoop()
    {
        var registry = new StudioBookingRegistry();
        registry.Release(Guid.NewGuid());
        Assert.Empty(registry.ActiveJobs);
    }

    [TestMethod]
    public void IsBookedOrGpuBlocked_SeesOwnBooking_AndForeignGpuLease()
    {
        var registry = new StudioBookingRegistry();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();

        Assert.False(registry.IsBookedOrGpuBlocked(studioA, "gpu:local"));

        registry.TryBook(studioA, "gpu:local", Job("job A", "gpu:local"));

        Assert.True(registry.IsBookedOrGpuBlocked(studioA, "gpu:local"));
        Assert.True(registry.IsBookedOrGpuBlocked(studioB, "gpu:local"));
        Assert.False(registry.IsBookedOrGpuBlocked(studioB, "gpu:remote"));
        Assert.False(registry.IsBookedOrGpuBlocked(studioB, gpuResourceGroup: null));
    }

    [TestMethod]
    public void TryGetGpuBlocker_ReturnsTheHoldingJob()
    {
        var registry = new StudioBookingRegistry();
        registry.TryBook(Guid.NewGuid(), "gpu:local", Job("voicing intro", "gpu:local"));

        Assert.True(registry.TryGetGpuBlocker("gpu:local", out var blocker));
        Assert.Equal("voicing intro", blocker.Label);
        Assert.False(registry.TryGetGpuBlocker("gpu:remote", out _));
        Assert.False(registry.TryGetGpuBlocker(null, out _));
    }

    [TestMethod]
    public void TryUpdateProgress_TrimsValue_AndReportsWhetherAnythingChanged()
    {
        var registry = new StudioBookingRegistry();
        var studio = Guid.NewGuid();
        registry.TryBook(studio, null, Job("recording"));

        Assert.True(registry.TryUpdateProgress(studio, "  42%  "));
        Assert.Equal("42%", registry.ActiveJobs[studio].Progress);
        Assert.False(registry.TryUpdateProgress(studio, "42%"));
        Assert.False(registry.TryUpdateProgress(studio, "   "));
        Assert.False(registry.TryUpdateProgress(Guid.NewGuid(), "99%"));
    }
}

[TestClass]
public class StudioPendingOperationsTrackerTests
{
    [TestMethod]
    public async Task Add_Update_Remove_PublishEachTransition_AndKeepStartOrder()
    {
        var publisher = new CountingPublisher();
        var tracker = new StudioPendingOperationsTracker(publisher);

        var first = await tracker.AddAsync(
            StudioKind.WriterRoom, "  Writing text  ", StudioPendingOperationStatus.Waiting,
            "Waiting for GPU / previous studio job", "gpu:local", CancellationToken.None);
        var second = await tracker.AddAsync(
            StudioKind.Recording, "Recording music", StudioPendingOperationStatus.Waiting,
            detail: null, resourceGroup: "gpu:local", CancellationToken.None);
        Assert.Equal(2, publisher.Publishes);

        var pending = tracker.PendingOperations;
        Assert.Equal(2, pending.Count);
        Assert.Equal("Writing text", pending[0].Label);
        Assert.Equal(first, pending[0].Id);
        Assert.Equal(second, pending[1].Id);

        await tracker.UpdateAsync(
            first, StudioPendingOperationStatus.Work, "Running on default endpoint", "50%", CancellationToken.None);
        Assert.Equal(3, publisher.Publishes);
        var updated = tracker.PendingOperations.Single(operation => operation.Id == first);
        Assert.Equal(StudioPendingOperationStatus.Work, updated.Status);
        Assert.Equal("Running on default endpoint", updated.Detail);
        Assert.Equal("50%", updated.Progress);

        // Unknown ids neither throw nor publish.
        await tracker.UpdateAsync(Guid.NewGuid(), StudioPendingOperationStatus.Work, null, null, CancellationToken.None);
        Assert.Equal(3, publisher.Publishes);

        await tracker.RemoveAsync(first, CancellationToken.None);
        Assert.Equal(4, publisher.Publishes);
        await tracker.RemoveAsync(first, CancellationToken.None);
        Assert.Equal(4, publisher.Publishes);
        Assert.Equal(1, tracker.PendingOperations.Count);
    }

    [TestMethod]
    public void DisplayHelpers_DescribeKindsAndModelSwitches()
    {
        Assert.Equal("Writing text", StudioPendingOperationsTracker.DefaultLabel(StudioKind.WriterRoom));
        Assert.Equal("Voicing audio", StudioPendingOperationsTracker.DefaultLabel(StudioKind.VoiceBooth));
        Assert.Equal("Recording music", StudioPendingOperationsTracker.DefaultLabel(StudioKind.Recording));

        Assert.Equal(StudioPendingOperationStatus.Work, StudioPendingOperationsTracker.ActiveStatus(StudioKind.WriterRoom));
        Assert.Equal(StudioPendingOperationStatus.Recording, StudioPendingOperationsTracker.ActiveStatus(StudioKind.VoiceBooth));

        Assert.Equal("Preparing Voice Booth endpoint", StudioPendingOperationsTracker.PreparingDetail(StudioKind.VoiceBooth));

        var scheduler = new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance);
        var coldStart = new LocalGpuScheduler.GpuLease(scheduler, "gpu:local", "WriterRoom", previousAffinity: null);
        Assert.Equal("Loading Writer Room model", StudioPendingOperationsTracker.ModelSwitchDetail(StudioKind.WriterRoom, coldStart));

        var switching = new LocalGpuScheduler.GpuLease(scheduler, "gpu:local", "VoiceBooth", previousAffinity: "WriterRoom");
        Assert.Equal(
            "Switching from Writer Room to Voice Booth",
            StudioPendingOperationsTracker.ModelSwitchDetail(StudioKind.VoiceBooth, switching));
    }

    private sealed class CountingPublisher : IStudioUpdatePublisher
    {
        public int Publishes { get; private set; }

        public Task PublishStudiosChangedAsync(CancellationToken ct = default)
        {
            Publishes++;
            return Task.CompletedTask;
        }
    }
}
