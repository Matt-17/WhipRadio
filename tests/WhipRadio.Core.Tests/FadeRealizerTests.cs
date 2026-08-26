using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class FadeRealizerTests
{
    private static readonly PcmFormat Format = new();

    [TestMethod]
    public void FadeToSilence_RampsFromTheCurrentGain_AndTrimsTheEnd()
    {
        var end = Format.SecondsToSamples(10);
        var envelope = EnvelopeFactory.FullLevel(Format, 0, end);
        var fadeStart = Format.SecondsToSamples(2);
        var fadeEnd = Format.SecondsToSamples(3);

        var newEnd = FadeRealizer.FadeToSilence(envelope, end, fadeStart, fadeEnd);

        Assert.Equal(fadeEnd, newEnd);
        Assert.Equal(1f, envelope.GainAt(fadeStart), 3);
        Assert.Equal(0.5f, envelope.GainAt(Format.SecondsToSamples(2.5)), 3);
        Assert.Equal(0f, envelope.GainAt(fadeEnd), 3);
        Assert.Equal(0f, envelope.GainAt(Format.SecondsToSamples(5)), 3);
    }

    [TestMethod]
    public void FadeToSilence_NeverExtendsASourcePastItsOwnEnd()
    {
        var end = Format.SecondsToSamples(2.5);
        var envelope = EnvelopeFactory.FullLevel(Format, 0, end);

        var newEnd = FadeRealizer.FadeToSilence(
            envelope, end, Format.SecondsToSamples(2), Format.SecondsToSamples(3));

        Assert.Equal(end, newEnd);
    }
}
