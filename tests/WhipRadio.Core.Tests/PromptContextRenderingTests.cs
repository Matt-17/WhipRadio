using WhipRadio.Core.Prompting;

namespace WhipRadio.Core.Tests;

[TestClass]
public class PromptContextRenderingTests
{
    [TestMethod]
    public void RenderSituation_ListsCurrentShowTracksWithAntiRepeatInstruction()
    {
        var context = new PromptContext
        {
            StationName = "WhipRadio",
            FrequencyMhz = 104.4,
            Purpose = "test",
            CurrentShowTracks = ["Glass Harbor - Afterimage (synth pop), 21:04"],
        };

        var rendered = context.RenderSituation();

        Assert.Contains("Tracks already aired in this show", rendered);
        Assert.Contains("do NOT reintroduce or back-announce these as if new", rendered);
        Assert.Contains("Glass Harbor - Afterimage (synth pop), 21:04", rendered);
    }

    [TestMethod]
    public void RenderSituation_ListsPreviousShowTracksWithAntiRepeatInstruction()
    {
        var context = new PromptContext
        {
            StationName = "WhipRadio",
            FrequencyMhz = 104.4,
            Purpose = "test",
            PreviousShowTracks = ["Night Drift - Cobalt (ambient), 19:30"],
        };

        var rendered = context.RenderSituation();

        Assert.Contains("Tracks aired in the previous show", rendered);
        Assert.Contains("do NOT reintroduce or back-announce these as if new", rendered);
        Assert.Contains("Night Drift - Cobalt (ambient), 19:30", rendered);
    }

    [TestMethod]
    public void RenderSituation_OmitsAiredTracksSectionWhenEmpty()
    {
        var context = new PromptContext
        {
            StationName = "WhipRadio",
            FrequencyMhz = 104.4,
            Purpose = "test",
        };

        var rendered = context.RenderSituation();

        Assert.DoesNotContain("Tracks already aired in this show", rendered);
        Assert.DoesNotContain("Tracks aired in the previous show", rendered);
    }
}
