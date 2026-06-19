using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class MusicProductionControlTests
{
    [TestMethod]
    public void RequeueTrackForFront_RestoresDequeuedRequestBeforeLaterQueuedRequests()
    {
        var control = new MusicProductionControl();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        control.RequestTrackFor(first);
        control.RequestTrackFor(second);

        Assert.Equal(first, control.TryDequeueManualRequest());
        control.RequeueTrackForFront(first);

        Assert.Equal(new[] { first, second }, control.QueuedArtistIds());
        Assert.Equal(first, control.TryPeekManualRequest());
        Assert.Equal(first, control.TryDequeueManualRequest());
        Assert.Equal(second, control.TryDequeueManualRequest());
        Assert.Null(control.TryDequeueManualRequest());
    }
}
