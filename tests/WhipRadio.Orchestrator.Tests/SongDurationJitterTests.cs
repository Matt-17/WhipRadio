using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class SongDurationJitterTests
{
    [TestMethod]
    public void CandidateOffsets_AvoidRoundTenDurationsWhenPossible()
    {
        var offsets = SongDurationJitter.CandidateOffsets(240, 150, 480);

        Assert.DoesNotContain(0, offsets);
        Assert.DoesNotContain(-10, offsets);
        Assert.DoesNotContain(10, offsets);
        Assert.True(offsets.All(offset => (240 + offset) % 10 != 0));
        Assert.True(offsets.All(offset => Math.Abs(offset) <= SongDurationJitter.MaxJitterSeconds));
    }

    [TestMethod]
    public void Apply_ClampsAtStationBounds()
    {
        Assert.Equal(150, SongDurationJitter.Apply(150, 150, 480, -7));
        Assert.Equal(480, SongDurationJitter.Apply(480, 150, 480, 7));
    }

    [TestMethod]
    public void Apply_UsesOnlyValidOffsetsNearMinimum()
    {
        var offsets = SongDurationJitter.CandidateOffsets(150, 150, 480);

        Assert.True(offsets.All(offset => offset > 0));
        Assert.True(offsets.All(offset => 150 + offset >= 150));
        Assert.True(offsets.All(offset => (150 + offset) % 10 != 0));
    }
}
