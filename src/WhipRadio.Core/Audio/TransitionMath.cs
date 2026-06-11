namespace WhipRadio.Core.Audio;

/// <summary>Seedable randomness for deterministic planner/math tests.</summary>
public interface IRandomSource
{
    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    int NextInt(int minInclusive, int maxExclusive);

    /// <summary>Uniform double in [0, 1).</summary>
    double NextDouble();
}

public sealed class SystemRandomSource(int? seed = null) : IRandomSource
{
    private readonly Random _random = seed is { } s ? new Random(s) : Random.Shared;

    public int NextInt(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

    public double NextDouble() => _random.NextDouble();
}

/// <summary>Pure scheduling math for transitions — every formula unit-tested.</summary>
public static class TransitionMath
{
    /// <summary>Anti-click protection on every source start/stop (not a "fade").</summary>
    public const int AntiClickRampMs = 15;

    /// <summary>
    /// "Hit the post": where in the incoming song the talk starts so it ends
    /// PostHitSafetyMs before the intro energy kicks in. Clamped at 0.
    /// </summary>
    public static double TalkStartInSong(double introEndSeconds, double talkDurationSeconds, int postHitSafetyMs)
        => Math.Max(0, introEndSeconds - talkDurationSeconds - postHitSafetyMs / 1000.0);

    /// <summary>IntroTalkOver eligibility: the intro must host at least half the talk.</summary>
    public static bool CanHitThePost(double introEndSeconds, double talkDurationSeconds)
        => introEndSeconds >= talkDurationSeconds * 0.5;

    /// <summary>Nearest beat in the grid to the anchor time (seconds).</summary>
    public static double NearestBeat(IReadOnlyList<double> beatGrid, double anchorSeconds)
    {
        if (beatGrid.Count == 0)
        {
            return anchorSeconds;
        }

        var best = beatGrid[0];
        var bestDistance = Math.Abs(best - anchorSeconds);
        for (var i = 1; i < beatGrid.Count; i++)
        {
            var distance = Math.Abs(beatGrid[i] - anchorSeconds);
            if (distance < bestDistance)
            {
                best = beatGrid[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Beat alignment without stretching: the incoming source starts so that its
    /// first audible beat lands exactly on the chosen outgoing beat.
    /// </summary>
    public static long IncomingStartMasterSample(
        long masterSampleOfOutgoingBeat, double incomingFirstBeatSeconds, int sampleRate)
        => masterSampleOfOutgoingBeat - (long)Math.Round(incomingFirstBeatSeconds * sampleRate);

    /// <summary>Crossfade window in outgoing beats: round(seconds·bpm/60), clamped 4–16.</summary>
    public static int CrossfadeBeats(double crossfadeSeconds, double outgoingBpm)
        => Math.Clamp((int)Math.Round(crossfadeSeconds * outgoingBpm / 60.0), 4, 16);

    /// <summary>HardCut gap drawn uniformly from the configured range (ms).</summary>
    public static int SampleGapMs(IRandomSource random, int minMs, int maxMs)
        => minMs >= maxMs ? minMs : random.NextInt(minMs, maxMs + 1);

    /// <summary>
    /// Loudness-normalization makeup gain (linear). Items without analysis get 1.0.
    /// </summary>
    public static float MakeupGainLinear(double? integratedLufs, double targetLufs, double maxMakeupGainDb)
    {
        if (integratedLufs is not { } lufs)
        {
            return 1f;
        }

        var makeupDb = Math.Clamp(targetLufs - lufs, -maxMakeupGainDb, maxMakeupGainDb);
        return (float)Math.Pow(10, makeupDb / 20.0);
    }

    /// <summary>Decibels → linear gain.</summary>
    public static float DbToLinear(double db) => (float)Math.Pow(10, db / 20.0);
}

/// <summary>
/// Builds gain envelopes for the strategies — anti-click micro-ramps are added
/// here on EVERY start and stop, invisible to the strategy layer.
/// </summary>
public static class EnvelopeFactory
{
    /// <summary>Plain playback: ramp in at start, ramp out at the known end.</summary>
    public static GainEnvelope FullLevel(PcmFormat format, long startSample, long endSample)
    {
        var ramp = RampSamples(format);
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(startSample, 0f, RampShape.Linear);
        envelope.AddBreakpoint(startSample + ramp, 1f, RampShape.Hold);
        envelope.AddBreakpoint(Math.Max(startSample + ramp, endSample - ramp), 1f, RampShape.Linear);
        envelope.AddBreakpoint(endSample, 0f, RampShape.Hold);
        return envelope;
    }

    /// <summary>Outgoing side of an equal-power crossfade ending at fadeEnd.</summary>
    public static GainEnvelope FadeOut(PcmFormat format, long startSample, long fadeStartSample, long fadeEndSample)
    {
        var ramp = RampSamples(format);
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(startSample, 0f, RampShape.Linear);
        envelope.AddBreakpoint(startSample + ramp, 1f, RampShape.Hold);
        envelope.AddBreakpoint(fadeStartSample, 1f, RampShape.EqualPowerOut);
        envelope.AddBreakpoint(fadeEndSample, 0f, RampShape.Hold);
        return envelope;
    }

    /// <summary>Incoming side of an equal-power crossfade starting at fadeStart.</summary>
    public static GainEnvelope FadeIn(PcmFormat format, long fadeStartSample, long fadeEndSample, long endSample)
    {
        var ramp = RampSamples(format);
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(fadeStartSample, 0f, RampShape.EqualPowerIn);
        envelope.AddBreakpoint(fadeEndSample, 1f, RampShape.Hold);
        envelope.AddBreakpoint(Math.Max(fadeEndSample, endSample - ramp), 1f, RampShape.Linear);
        envelope.AddBreakpoint(endSample, 0f, RampShape.Hold);
        return envelope;
    }

    /// <summary>
    /// Ducked bed under talk: full level, dip to duckLevel during [duckStart,
    /// duckEnd], release ramp scheduled to END exactly at duckEnd.
    /// </summary>
    public static GainEnvelope DuckedBed(
        PcmFormat format, long startSample, long endSample,
        long duckStartSample, long duckEndSample, double duckLevelDb, int duckRampMs)
    {
        var ramp = RampSamples(format);
        var duckRamp = format.SecondsToSamples(duckRampMs / 1000.0);
        var duckGain = TransitionMath.DbToLinear(duckLevelDb);

        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(startSample, 0f, RampShape.Linear);
        envelope.AddBreakpoint(startSample + ramp, 1f, RampShape.Hold);
        envelope.AddBreakpoint(Math.Max(startSample + ramp, duckStartSample - duckRamp), 1f, RampShape.Linear);
        envelope.AddBreakpoint(duckStartSample, duckGain, RampShape.Hold);
        envelope.AddBreakpoint(Math.Max(duckStartSample, duckEndSample - duckRamp), duckGain, RampShape.Linear);
        envelope.AddBreakpoint(duckEndSample, 1f, RampShape.Hold);
        envelope.AddBreakpoint(Math.Max(duckEndSample, endSample - ramp), 1f, RampShape.Linear);
        envelope.AddBreakpoint(endSample, 0f, RampShape.Hold);
        return envelope;
    }

    public static long RampSamples(PcmFormat format)
        => format.SecondsToSamples(TransitionMath.AntiClickRampMs / 1000.0);
}
