using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Tests;

public class SpeechMarkerPlainTextTests
{
    [Fact]
    public void ToPlainText_PausesBecomeEllipses_BreathAndRateDropped()
    {
        var result = SpeechMarkerNormalizer.ToPlainText(
            "Hello [pause:400ms] there [breath] friends [rate:slow] tonight.");

        Assert.Equal("Hello … there friends tonight.", result);
    }

    [Fact]
    public void Normalize_BreathDisabled_StripsBreathMarkers()
    {
        var result = SpeechMarkerNormalizer.Normalize("Take [breath] a moment [pause:300ms] now.", allowBreath: false);

        Assert.Equal("Take a moment [pause:300ms] now.", result);
    }
}
