using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

[TestClass]
public class SpeechMarkerNormalizerTests
{
    [TestMethod]
    public void Normalize_KeepsValidMarkers_ScalingPause()
    {
        // Pauses are scaled down (0.65); breath/rate markers pass through untouched.
        var input = "Hello [pause:400ms] world [breath] again [rate:slow] done";
        Assert.Equal("Hello [pause:260ms] world [breath] again [rate:slow] done", SpeechMarkerNormalizer.Normalize(input));
    }

    [TestMethod]
    public void Normalize_StripsUnknownTags()
    {
        var result = SpeechMarkerNormalizer.Normalize("Hello [laughs] world [music swells] end");
        Assert.Equal("Hello world end", result);
    }

    [TestMethod]
    [DataRow("[pause:50ms]", "[pause:100ms]")]    // scaled below minimum → clamped up
    [DataRow("[pause:9000ms]", "[pause:1500ms]")] // scaled still above maximum → clamped down
    [DataRow("[pause:100ms]", "[pause:100ms]")]   // 65 → clamped to floor
    [DataRow("[pause:1500ms]", "[pause:975ms]")]  // 1500 × 0.65
    [DataRow("[pause:300]", "[pause:195ms]")]     // missing ms suffix tolerated, then scaled
    public void Normalize_ScalesAndClampsPauseDurations(string input, string expected)
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
        Assert.Equal("Hi [pause:130ms] there [breath] you", result);
    }

    [TestMethod]
    public void Normalize_InsertsPauseBetweenParagraphs()
    {
        var result = SpeechMarkerNormalizer.Normalize("First item.\n\nSecond item.");
        Assert.Equal("First item. [pause:650ms] Second item.", result);
    }

    [TestMethod]
    public void Normalize_SingleNewlineIsAlsoAParagraphBreak()
    {
        var result = SpeechMarkerNormalizer.Normalize("Line one.\nLine two.");
        Assert.Equal("Line one. [pause:650ms] Line two.", result);
    }

    [TestMethod]
    public void Normalize_CollapsesAdjacentPausesToTheLongest()
    {
        // A model pause right at a paragraph break must not stack with the inserted one.
        var result = SpeechMarkerNormalizer.Normalize("End. [pause:400ms]\n\nNext.");
        Assert.Equal("End. [pause:650ms] Next.", result);
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
        Assert.Equal("Um, that was, hm, really good [pause:195ms] right?", SpeechMarkerNormalizer.Normalize(input));
    }

    [TestMethod]
    public void StripMarkers_RemovesAllMarkers()
    {
        var result = SpeechMarkerNormalizer.StripMarkers("Up next, uh [pause:300ms] a banger [breath] now [rate:slow]!");
        Assert.Equal("Up next, uh a banger now !", result);
    }
}
