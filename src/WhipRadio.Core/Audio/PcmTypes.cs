namespace WhipRadio.Core.Audio;

/// <summary>Canonical pipeline format: 44.1 kHz stereo interleaved s16le.</summary>
public sealed record PcmFormat(int SampleRate = 44100, int Channels = 2)
{
    /// <summary>Frame size in samples per channel (~23.2 ms at 44.1 kHz).</summary>
    public const int FrameSamples = 1024;

    public long SecondsToSamples(double seconds) => (long)Math.Round(seconds * SampleRate);

    public double SamplesToSeconds(long samples) => samples / (double)SampleRate;
}

public enum RampShape
{
    /// <summary>Constant gain until the next breakpoint.</summary>
    Hold,

    Linear,

    /// <summary>Incoming curve: sin(x·π/2) — fast rise, slow settle.</summary>
    EqualPowerIn,

    /// <summary>Outgoing curve: cos(x·π/2) — slow drop, fast tail.</summary>
    EqualPowerOut,
}

/// <summary>
/// Piecewise gain curve over the MASTER sample clock. Breakpoints carry the
/// shape used to interpolate toward the NEXT breakpoint; before the first
/// breakpoint the first gain holds, after the last the last gain holds.
/// </summary>
public sealed class GainEnvelope
{
    private readonly List<(long Pos, float Gain, RampShape Shape)> _points = [];

    public int Count => _points.Count;

    public void AddBreakpoint(long samplePos, float gain, RampShape shapeToNext)
    {
        var index = _points.FindLastIndex(p => p.Pos <= samplePos);
        if (index >= 0 && _points[index].Pos == samplePos)
        {
            _points[index] = (samplePos, gain, shapeToNext);
            return;
        }

        _points.Insert(index + 1, (samplePos, gain, shapeToNext));
    }

    /// <summary>Drops all breakpoints at or after the position — used when a
    /// transition re-plans an item's ending (e.g. crossfade replaces end ramp).</summary>
    public void RemoveBreakpointsFrom(long samplePos)
        => _points.RemoveAll(p => p.Pos >= samplePos);

    public float GainAt(long samplePos)
    {
        if (_points.Count == 0)
        {
            return 1f;
        }

        // Binary search for the last breakpoint at or before samplePos.
        int lo = 0, hi = _points.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_points[mid].Pos <= samplePos)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return _points[0].Gain;
        }

        if (found == _points.Count - 1)
        {
            return _points[found].Gain;
        }

        var (p0, g0, shape) = _points[found];
        var (p1, g1, _) = _points[found + 1];
        if (shape == RampShape.Hold || p1 == p0)
        {
            return g0;
        }

        var x = (float)((samplePos - p0) / (double)(p1 - p0));
        return shape switch
        {
            RampShape.Linear => g0 + (g1 - g0) * x,
            RampShape.EqualPowerIn => g0 + (g1 - g0) * MathF.Sin(x * MathF.PI / 2f),
            RampShape.EqualPowerOut => g1 + (g0 - g1) * MathF.Cos(x * MathF.PI / 2f),
            _ => g0,
        };
    }
}

/// <summary>Pull-model PCM source (decoder stdout behind a ring buffer).</summary>
public interface IPcmSampleReader
{
    /// <summary>Fills frame with up to frame.Length interleaved samples; returns
    /// samples written. 0 = end of stream. A partial read with
    /// <see cref="EndOfStream"/> false is an underrun.</summary>
    int Read(Span<short> frame);

    /// <summary>True once the underlying stream has ended (a final partial read
    /// is a natural end, not an underrun).</summary>
    bool EndOfStream { get; }
}

/// <summary>One mixer input: a PCM source scheduled on the master clock.</summary>
public sealed class SourceSlot
{
    public required IPcmSampleReader Reader { get; init; }

    public required GainEnvelope Envelope { get; init; }

    /// <summary>Master sample position at which this source begins.</summary>
    public required long StartAtMasterSample { get; init; }

    /// <summary>Samples (per channel) to skip into the file (silence trim / talk-over offset).</summary>
    public long SourceOffsetSamples { get; init; }

    /// <summary>Loudness-normalization gain (linear).</summary>
    public float MakeupGainLinear { get; init; } = 1f;

    /// <summary>Identifies the playout item for event emission.</summary>
    public object? Tag { get; init; }

    public bool Finished { get; private set; }

    public void MarkFinished() => Finished = true;
}
