namespace WhipRadio.Core.Audio;

/// <summary>
/// The shared "clear this source off the air" envelope edit used by the off-air
/// fade, the top-of-hour hold fade, and the timed-interrupt fade.
/// </summary>
public static class FadeRealizer
{
    /// <summary>
    /// Replaces everything from <paramref name="fadeStart"/> with a linear ramp
    /// from the gain the source has there down to silence at <paramref name="fadeEnd"/>,
    /// and returns the source's trimmed end (never later than the fade end).
    /// </summary>
    public static long FadeToSilence(GainEnvelope envelope, long endAtMaster, long fadeStart, long fadeEnd)
    {
        var currentGain = envelope.GainAt(fadeStart);
        envelope.RemoveBreakpointsFrom(fadeStart);
        envelope.AddBreakpoint(fadeStart, currentGain, RampShape.Linear);
        envelope.AddBreakpoint(fadeEnd, 0f, RampShape.Hold);
        return Math.Min(endAtMaster, fadeEnd);
    }
}
