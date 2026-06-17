using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class GainEnvelopeTests
{
    [TestMethod]
    public void EmptyEnvelope_IsUnityGain()
    {
        var envelope = new GainEnvelope();
        Assert.Equal(1f, envelope.GainAt(0));
        Assert.Equal(1f, envelope.GainAt(123456));
    }

    [TestMethod]
    public void BeforeFirstBreakpoint_HoldsFirstGain()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(1000, 0.5f, RampShape.Hold);
        Assert.Equal(0.5f, envelope.GainAt(0));
    }

    [TestMethod]
    public void AfterLastBreakpoint_HoldsLastGain()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(0, 0f, RampShape.Linear);
        envelope.AddBreakpoint(100, 0.8f, RampShape.Hold);
        Assert.Equal(0.8f, envelope.GainAt(5000));
    }

    [TestMethod]
    public void Hold_KeepsGainUntilNextBreakpoint()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(0, 0.3f, RampShape.Hold);
        envelope.AddBreakpoint(1000, 0.9f, RampShape.Hold);
        Assert.Equal(0.3f, envelope.GainAt(999));
        Assert.Equal(0.9f, envelope.GainAt(1000));
    }

    [TestMethod]
    public void Linear_InterpolatesProportionally()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(0, 0f, RampShape.Linear);
        envelope.AddBreakpoint(1000, 1f, RampShape.Hold);
        Assert.Equal(0.5f, envelope.GainAt(500), 3);
        Assert.Equal(0.25f, envelope.GainAt(250), 3);
    }

    [TestMethod]
    public void EqualPowerCurves_AreComplementaryInPower()
    {
        var fadeOut = new GainEnvelope();
        fadeOut.AddBreakpoint(0, 1f, RampShape.EqualPowerOut);
        fadeOut.AddBreakpoint(10_000, 0f, RampShape.Hold);

        var fadeIn = new GainEnvelope();
        fadeIn.AddBreakpoint(0, 0f, RampShape.EqualPowerIn);
        fadeIn.AddBreakpoint(10_000, 1f, RampShape.Hold);

        // g_out² + g_in² = 1 over the whole curve (checked at 100 points).
        for (var i = 0; i <= 100; i++)
        {
            var pos = i * 100L;
            var gOut = fadeOut.GainAt(pos);
            var gIn = fadeIn.GainAt(pos);
            Assert.Equal(1f, gOut * gOut + gIn * gIn, 3);
        }
    }

    [TestMethod]
    public void DuplicateBreakpointPosition_ReplacesExisting()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(100, 0.2f, RampShape.Hold);
        envelope.AddBreakpoint(100, 0.7f, RampShape.Hold);
        Assert.Equal(0.7f, envelope.GainAt(100));
        Assert.Equal(1, envelope.Count);
    }

    [TestMethod]
    public void OutOfOrderInsertion_StaysSorted()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(2000, 1f, RampShape.Hold);
        envelope.AddBreakpoint(0, 0f, RampShape.Linear);
        envelope.AddBreakpoint(1000, 0.5f, RampShape.Linear);
        Assert.Equal(0.25f, envelope.GainAt(500), 3);
        Assert.Equal(0.75f, envelope.GainAt(1500), 3);
    }
}
