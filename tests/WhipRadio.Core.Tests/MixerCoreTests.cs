using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

public class MixerCoreTests
{
    private static readonly PcmFormat Mono = new(SampleRate: 44100, Channels: 1);

    private sealed class FakeReader(short[] data) : IPcmSampleReader
    {
        private int _pos;

        public int StallAfter { get; set; } = int.MaxValue;

        public bool EndOfStream => _pos >= data.Length && StallAfter == int.MaxValue;

        public int Read(Span<short> frame)
        {
            if (_pos >= StallAfter)
            {
                return 0; // mid-stream stall (underrun), EndOfStream stays false
            }

            var count = Math.Min(frame.Length, data.Length - _pos);
            if (count <= 0)
            {
                return 0;
            }

            data.AsSpan(_pos, count).CopyTo(frame);
            _pos += count;
            return count;
        }
    }

    private static GainEnvelope Unity()
    {
        var envelope = new GainEnvelope();
        envelope.AddBreakpoint(0, 1f, RampShape.Hold);
        return envelope;
    }

    private static (short[] Output, MixerCore Core) Mix(
        PcmFormat format, IReadOnlyList<SourceSlot> slots, long masterPos, int frameSamples = 1024)
    {
        var core = new MixerCore(format);
        var output = new short[frameSamples * format.Channels];
        var accumulator = new float[output.Length];
        var scratch = new short[output.Length];
        core.MixFrame(masterPos, slots, output, accumulator, scratch);
        return (output, core);
    }

    [Fact]
    public void SingleSource_PassesThroughAtUnityGain()
    {
        var data = Enumerable.Range(0, 1024).Select(i => (short)(i % 1000)).ToArray();
        var slot = new SourceSlot { Reader = new FakeReader(data), Envelope = Unity(), StartAtMasterSample = 0 };

        var (output, _) = Mix(Mono, [slot], masterPos: 0);

        Assert.Equal(data, output);
    }

    [Fact]
    public void TwoSources_Sum()
    {
        var a = new short[1024];
        var b = new short[1024];
        Array.Fill(a, (short)1000);
        Array.Fill(b, (short)2000);
        var slots = new[]
        {
            new SourceSlot { Reader = new FakeReader(a), Envelope = Unity(), StartAtMasterSample = 0 },
            new SourceSlot { Reader = new FakeReader(b), Envelope = Unity(), StartAtMasterSample = 0 },
        };

        var (output, core) = Mix(Mono, slots, masterPos: 0);

        Assert.All(output, s => Assert.Equal(3000, s));
        Assert.Equal(0, core.ClipCount);
    }

    [Fact]
    public void Summation_ClampsAndCountsClips()
    {
        var a = new short[1024];
        var b = new short[1024];
        Array.Fill(a, (short)30000);
        Array.Fill(b, (short)30000);
        var slots = new[]
        {
            new SourceSlot { Reader = new FakeReader(a), Envelope = Unity(), StartAtMasterSample = 0 },
            new SourceSlot { Reader = new FakeReader(b), Envelope = Unity(), StartAtMasterSample = 0 },
        };

        var (output, core) = Mix(Mono, slots, masterPos: 0);

        Assert.All(output, s => Assert.Equal(short.MaxValue, s));
        Assert.Equal(1024, core.ClipCount);
    }

    [Fact]
    public void MakeupGain_Applies()
    {
        var data = new short[1024];
        Array.Fill(data, (short)1000);
        var slot = new SourceSlot
        {
            Reader = new FakeReader(data),
            Envelope = Unity(),
            StartAtMasterSample = 0,
            MakeupGainLinear = 2f,
        };

        var (output, _) = Mix(Mono, [slot], masterPos: 0);

        Assert.All(output, s => Assert.Equal(2000, s));
    }

    [Fact]
    public void SourceStartingMidFrame_IsSilentBeforeStart()
    {
        var data = new short[1024];
        Array.Fill(data, (short)5000);
        var slot = new SourceSlot { Reader = new FakeReader(data), Envelope = Unity(), StartAtMasterSample = 512 };

        var (output, _) = Mix(Mono, [slot], masterPos: 0);

        Assert.All(output[..512], s => Assert.Equal(0, s));
        Assert.All(output[512..], s => Assert.Equal(5000, s));
    }

    [Fact]
    public void SourceStartingAfterFrame_ContributesNothing()
    {
        var data = new short[1024];
        Array.Fill(data, (short)5000);
        var slot = new SourceSlot { Reader = new FakeReader(data), Envelope = Unity(), StartAtMasterSample = 99999 };

        var (output, _) = Mix(Mono, [slot], masterPos: 0);

        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void NaturalEndOfStream_FinishesWithoutUnderrun()
    {
        var data = new short[500]; // less than a frame
        Array.Fill(data, (short)100);
        var slot = new SourceSlot { Reader = new FakeReader(data), Envelope = Unity(), StartAtMasterSample = 0 };

        var (output, core) = Mix(Mono, [slot], masterPos: 0);

        Assert.Equal(100, output[499]);
        Assert.Equal(0, output[500]);
        Assert.Equal(0, core.UnderrunCount);
        Assert.True(slot.Finished);
    }

    [Fact]
    public void MidStreamStall_ZeroFillsAndCountsUnderrun()
    {
        var data = new short[5000];
        Array.Fill(data, (short)100);
        var reader = new FakeReader(data) { StallAfter = 0 }; // stalls immediately, not EOF
        var slot = new SourceSlot { Reader = reader, Envelope = Unity(), StartAtMasterSample = 0 };

        var (output, core) = Mix(Mono, [slot], masterPos: 0);

        Assert.All(output, s => Assert.Equal(0, s));
        Assert.Equal(1, core.UnderrunCount);
        Assert.False(slot.Finished); // never finish on a stall — the clock keeps running
    }

    [Fact]
    public void HardCutEnvelope_HasAntiClickRamps()
    {
        var format = new PcmFormat();
        var end = format.SecondsToSamples(10);
        var envelope = EnvelopeFactory.FullLevel(format, 0, end);
        var ramp = EnvelopeFactory.RampSamples(format);

        Assert.Equal(0f, envelope.GainAt(0));
        Assert.Equal(1f, envelope.GainAt(ramp));
        Assert.Equal(1f, envelope.GainAt(end - ramp));
        Assert.Equal(0f, envelope.GainAt(end));
        // ramp is ~15 ms — strictly between 0 and full inside the ramp window
        var mid = envelope.GainAt(ramp / 2);
        Assert.InRange(mid, 0.01f, 0.99f);
    }

    [Fact]
    public void DuckedBed_ReleaseEndsExactlyAtDuckEnd()
    {
        var format = new PcmFormat();
        var duckStart = format.SecondsToSamples(5);
        var duckEnd = format.SecondsToSamples(15);
        var envelope = EnvelopeFactory.DuckedBed(
            format, 0, format.SecondsToSamples(60), duckStart, duckEnd, duckLevelDb: -12, duckRampMs: 800);

        var duckGain = TransitionMath.DbToLinear(-12);
        Assert.Equal(duckGain, envelope.GainAt(duckStart), 3);
        Assert.Equal(1f, envelope.GainAt(duckEnd), 3);
        // mid-release: strictly between duck level and full
        var midRelease = envelope.GainAt(duckEnd - format.SecondsToSamples(0.4));
        Assert.InRange(midRelease, duckGain + 0.01f, 0.99f);
    }

    [Fact]
    public void GoldenTest_EqualPowerCrossfade_HoldsConstantRms()
    {
        // Two uncorrelated sines through a 2 s equal-power fade: the per-window
        // RMS of the mix must stay within ±5 % of a single source's RMS.
        var format = new PcmFormat(SampleRate: 44100, Channels: 1);
        const int seconds = 2;
        const float amplitude = 8000;
        var total = format.SampleRate * seconds;

        short[] Sine(double freq) => Enumerable.Range(0, total)
            .Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * freq * i / format.SampleRate)))
            .ToArray();

        var outgoing = new GainEnvelope();
        outgoing.AddBreakpoint(0, 1f, RampShape.EqualPowerOut);
        outgoing.AddBreakpoint(total, 0f, RampShape.Hold);
        var incoming = new GainEnvelope();
        incoming.AddBreakpoint(0, 0f, RampShape.EqualPowerIn);
        incoming.AddBreakpoint(total, 1f, RampShape.Hold);

        var slots = new[]
        {
            new SourceSlot { Reader = new FakeReader(Sine(440)), Envelope = outgoing, StartAtMasterSample = 0 },
            new SourceSlot { Reader = new FakeReader(Sine(587)), Envelope = incoming, StartAtMasterSample = 0 },
        };

        var core = new MixerCore(format);
        var frame = new short[PcmFormat.FrameSamples];
        var accumulator = new float[frame.Length];
        var scratch = new short[frame.Length];
        var mixed = new List<short>(total);
        for (long pos = 0; pos < total; pos += PcmFormat.FrameSamples)
        {
            core.MixFrame(pos, slots, frame, accumulator, scratch);
            mixed.AddRange(frame);
        }

        var expectedRms = amplitude / Math.Sqrt(2);
        var window = format.SampleRate / 10; // 100 ms
        // Skip the first/last windows (fade endpoints interact with sine phase).
        for (var w = 1; w < total / window - 1; w++)
        {
            double sum = 0;
            for (var i = w * window; i < (w + 1) * window; i++)
            {
                sum += (double)mixed[i] * mixed[i];
            }

            var rms = Math.Sqrt(sum / window);
            Assert.InRange(rms, expectedRms * 0.95, expectedRms * 1.05);
        }

        Assert.Equal(0, core.ClipCount);
    }

    [Fact]
    public void HotLoop_DoesNotAllocate()
    {
        var format = new PcmFormat(SampleRate: 44100, Channels: 2);
        var data = new short[44100 * 2];
        var slot = new SourceSlot { Reader = new FakeReader(data), Envelope = Unity(), StartAtMasterSample = 0 };
        var slots = new[] { slot };

        var core = new MixerCore(format);
        var output = new short[PcmFormat.FrameSamples * 2];
        var accumulator = new float[output.Length];
        var scratch = new short[output.Length];

        core.MixFrame(0, slots, output, accumulator, scratch); // warmup (JIT)

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (long pos = PcmFormat.FrameSamples; pos < 10 * PcmFormat.FrameSamples; pos += PcmFormat.FrameSamples)
        {
            core.MixFrame(pos, slots, output, accumulator, scratch);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"hot loop allocated {allocated} bytes");
    }
}
