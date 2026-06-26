using WhipRadio.Core.Api;

namespace WhipRadio.Core.Tests;

[TestClass]
public class StationPresentationTests
{
    [TestMethod]
    // Encoder lifecycle wins first, regardless of the operator switch / now-playing.
    [DataRow("Offline", true, true, StationPresentation.Offline)]
    [DataRow("Offline", false, false, StationPresentation.Offline)]
    [DataRow("offline", true, false, StationPresentation.Offline)] // case-insensitive
    [DataRow("Reconnecting", true, true, StationPresentation.Reconnecting)]
    [DataRow("Reconnecting", false, false, StationPresentation.Reconnecting)]
    // Online (or unknown) status: the operator's On Air intent gates on/off air.
    [DataRow("Online", false, true, StationPresentation.OffAir)]
    [DataRow("Online", true, true, StationPresentation.Live)]
    [DataRow("Online", true, false, StationPresentation.Standby)]
    // Null / unknown status is treated as online.
    [DataRow(null, true, true, StationPresentation.Live)]
    [DataRow(null, true, false, StationPresentation.Standby)]
    [DataRow(null, false, false, StationPresentation.OffAir)]
    [DataRow("something-else", true, false, StationPresentation.Standby)]
    public void Derive_FoldsInputsConsistently(
        string? status, bool playoutEnabled, bool hasNowPlaying, StationPresentation expected)
    {
        Assert.Equal(expected, StationPresentationState.Derive(status, playoutEnabled, hasNowPlaying));
    }
}
