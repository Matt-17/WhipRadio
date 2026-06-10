using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

public class SpeechMarkerNormalizerTests
{
    [Fact]
    public void Normalize_KeepsValidMarkers()
    {
        var input = "Hello [pause:400ms] world [breath] again [rate:slow] done";
        Assert.Equal(input, SpeechMarkerNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_StripsUnknownTags()
    {
        var result = SpeechMarkerNormalizer.Normalize("Hello [laughs] world [music swells] end");
        Assert.Equal("Hello world end", result);
    }

    [Theory]
    [InlineData("[pause:50ms]", "[pause:100ms]")]    // below minimum
    [InlineData("[pause:9000ms]", "[pause:1500ms]")] // above maximum
    [InlineData("[pause:100ms]", "[pause:100ms]")]   // boundary kept
    [InlineData("[pause:1500ms]", "[pause:1500ms]")] // boundary kept
    [InlineData("[pause:300]", "[pause:300ms]")]     // missing ms suffix tolerated
    public void Normalize_ClampsPauseDurations(string input, string expected)
    {
        Assert.Equal(expected, SpeechMarkerNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_CollapsesDuplicateBreathMarkers()
    {
        var result = SpeechMarkerNormalizer.Normalize("Take [breath] [breath][breath] a moment");
        Assert.Equal("Take [breath] a moment", result);
    }

    [Fact]
    public void Normalize_InvalidRateIsStripped()
    {
        var result = SpeechMarkerNormalizer.Normalize("Speak [rate:turbo] now [rate:fast] ok");
        Assert.Equal("Speak now [rate:fast] ok", result);
    }

    [Fact]
    public void Normalize_MalformedPauseIsStripped()
    {
        var result = SpeechMarkerNormalizer.Normalize("Wait [pause:abcms] here");
        Assert.Equal("Wait here", result);
    }

    [Fact]
    public void Normalize_UppercaseMarkersAreNormalized()
    {
        var result = SpeechMarkerNormalizer.Normalize("Hi [PAUSE:200MS] there [BREATH] you");
        Assert.Equal("Hi [pause:200ms] there [breath] you", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, SpeechMarkerNormalizer.Normalize(input!));
    }

    [Fact]
    public void Normalize_FillerWordsStayLiteral()
    {
        var input = "Äh, das war, hm, wirklich gut [pause:300ms] oder?";
        Assert.Equal(input, SpeechMarkerNormalizer.Normalize(input));
    }
}
