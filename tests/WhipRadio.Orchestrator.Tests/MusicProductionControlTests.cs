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

        var dequeued = control.TryDequeueManualRequest();
        Assert.Equal(first, dequeued?.ArtistId);
        control.RequeueTrackForFront(dequeued!);

        Assert.Equal(new[] { first, second }, control.QueuedArtistIds());
        Assert.Equal(first, control.TryPeekManualRequest()?.ArtistId);
        Assert.Equal(first, control.TryDequeueManualRequest()?.ArtistId);
        Assert.Equal(second, control.TryDequeueManualRequest()?.ArtistId);
        Assert.Null(control.TryDequeueManualRequest());
    }

    [TestMethod]
    public void ManualRequest_CarriesTheSongHintThroughTheQueue()
    {
        var control = new MusicProductionControl();
        var artistId = Guid.NewGuid();

        control.RequestTrackFor(new ManualSongRequest(artistId, "an indie track about ferries"));

        var request = control.TryDequeueManualRequest();
        Assert.Equal(artistId, request?.ArtistId);
        Assert.Equal("an indie track about ferries", request?.Hint);
    }
}
