using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

[TestClass]
public class SpeechMarkerNormalizerTests
{
    [TestMethod]
    public void Normalize_KeepsValidMarkers()
    {
        var input = "Hello [pause:400ms] world [breath] again [rate:slow] done";
        Assert.Equal(input, SpeechMarkerNormalizer.Normalize(input));
    }

    [TestMethod]
    public void Normalize_StripsUnknownTags()
    {
        var result = SpeechMarkerNormalizer.Normalize("Hello [laughs] world [music swells] end");
        Assert.Equal("Hello world end", result);
    }

    [TestMethod]
    [DataRow("[pause:50ms]", "[pause:100ms]")]    // below minimum
    [DataRow("[pause:9000ms]", "[pause:1500ms]")] // above maximum
    [DataRow("[pause:100ms]", "[pause:100ms]")]   // boundary kept
    [DataRow("[pause:1500ms]", "[pause:1500ms]")] // boundary kept
    [DataRow("[pause:300]", "[pause:300ms]")]     // missing ms suffix tolerated
    public void Normalize_ClampsPauseDurations(string input, string expected)
    {
        Assert.Equal(expected, SpeechMarkerNormalizer.Normalize(input));
    }

    [TestMethod]
    public void Normalize_CollapsesDuplicateBreathMarkers()
    {
        var result = SpeechMarkerNormalizer.Normalize("Take [breath] [breath][breath] a moment");
        Assert.Equal("Take [breath] a moment", result);
    }

    [TestMethod]
    public void Normalize_InvalidRateIsStripped()
    {
        var result = SpeechMarkerNormalizer.Normalize("Speak [rate:turbo] now [rate:fast] ok");
        Assert.Equal("Speak now [rate:fast] ok", result);
    }

    [TestMethod]
    public void Normalize_MalformedPauseIsStripped()
    {
        var result = SpeechMarkerNormalizer.Normalize("Wait [pause:abcms] here");
        Assert.Equal("Wait here", result);
    }

    [TestMethod]
    public void Normalize_UppercaseMarkersAreNormalized()
    {
        var result = SpeechMarkerNormalizer.Normalize("Hi [PAUSE:200MS] there [BREATH] you");
        Assert.Equal("Hi [pause:200ms] there [breath] you", result);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Normalize_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, SpeechMarkerNormalizer.Normalize(input!));
    }

    [TestMethod]
    public void Normalize_FillerWordsStayLiteral()
    {
        var input = "Um, that was, hm, really good [pause:300ms] right?";
        Assert.Equal(input, SpeechMarkerNormalizer.Normalize(input));
    }
}
