using System.Buffers.Binary;
using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class BedMixerTests
{
    private const int Rate = 8000; // small synthetic rate keeps fixtures tiny

    /// <summary>Mono 16-bit WAV holding a constant sample value.</summary>
    private static byte[] ConstantWav(short value, double seconds, int rate = Rate, short channels = 1)
    {
        var frames = (int)(seconds * rate);
        var pcm = new byte[frames * channels * 2];
        for (var i = 0; i < frames * channels; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }

        return WavFile.WrapPcm16(pcm, rate, channels);
    }

    private static short SampleAt(byte[] wav, double seconds)
    {
        var audio = WavFile.ParsePcm16Audio(wav);
        var frame = Math.Min((long)(seconds * audio.SampleRate), audio.FrameCount - 1);
        return BinaryPrimitives.ReadInt16LittleEndian(audio.Data.Span.Slice((int)(frame * audio.BytesPerFrame), 2));
    }

    private static readonly BedMixOptions Options = new(
        BedLevelDbUnderSpeech: -20,
        GapBetweenPartsSeconds: 2.0,
        LeadSeconds: 1.0,
        TailSeconds: 2.0,
        DuckRampMs: 200,
        FadeOutSeconds: 0.5);

    [TestMethod]
    public void Mix_TimelineLength_IsLeadPartsGapsAndTail()
    {
        var speech = new[] { ConstantWav(8000, 3.0), ConstantWav(8000, 4.0) };
        var bed = ConstantWav(10000, 1.5);

        var mixed = BedMixer.Mix(speech, bed, Options);

        // 1.0 lead + 3.0 + 2.0 gap + 4.0 + 2.0 tail = 12.0 s
        Assert.Equal(12.0, WavFile.GetDurationSeconds(mixed), precision: 2);
    }

    [TestMethod]
    public void Mix_BedIsDuckedUnderSpeechAndFullInTheOpen()
    {
        var speech = new[] { ConstantWav(0, 3.0) }; // silent speech isolates the bed level
        var bed = ConstantWav(10000, 1.5);

        var mixed = BedMixer.Mix(speech, bed, Options);

        // Mid-lead (0.3 s): before the ramp to the 1.0 s speech start → full bed.
        Assert.InRange(SampleAt(mixed, 0.3), 9500, 10000);
        // Mid-speech (2.5 s): ducked to −20 dB ≈ ×0.1.
        Assert.InRange(SampleAt(mixed, 2.5), 800, 1200);
        // Mid-tail (4.8 s): released back to full level.
        Assert.InRange(SampleAt(mixed, 4.8), 9500, 10000);
    }

    [TestMethod]
    public void Mix_SpeechRidesOnTopOfTheDuckedBed()
    {
        var speech = new[] { ConstantWav(5000, 2.0) };
        var bed = ConstantWav(10000, 1.0);

        var mixed = BedMixer.Mix(speech, bed, Options);

        // speech 5000 + ducked bed ~1000 ≈ 6000 mid-speech.
        Assert.InRange(SampleAt(mixed, 2.0), 5700, 6300);
    }

    [TestMethod]
    public void Mix_BedLoopsAcrossTheWholeBlock()
    {
        // A 0.5 s bed under an 8 s block only works if it loops; a non-looping
        // implementation would go silent after the first pass.
        var speech = new[] { ConstantWav(0, 6.0) };
        var bed = ConstantWav(10000, 0.5);

        var mixed = BedMixer.Mix(speech, bed, Options);

        Assert.InRange(Math.Abs((int)SampleAt(mixed, 4.0)), 800, 1200); // still audible mid-speech
    }

    [TestMethod]
    public void Mix_FadesOutAtTheVeryEnd()
    {
        var speech = new[] { ConstantWav(0, 2.0) };
        var bed = ConstantWav(10000, 1.0);

        var mixed = BedMixer.Mix(speech, bed, Options);
        var total = WavFile.GetDurationSeconds(mixed);

        Assert.True(Math.Abs((int)SampleAt(mixed, total - 0.02)) < 800,
            "the block must fade to (near) silence at its end");
    }

    [TestMethod]
    public void Mix_AdaptsStereoBedToMonoSpeech()
    {
        var speech = new[] { ConstantWav(0, 2.0) };
        var stereoBed = ConstantWav(10000, 1.0, rate: 16000, channels: 2);

        var mixed = BedMixer.Mix(speech, stereoBed, Options);
        var audio = WavFile.ParsePcm16Audio(mixed);

        Assert.Equal((short)1, audio.Channels);
        Assert.Equal(Rate, audio.SampleRate);
        Assert.InRange(Math.Abs((int)SampleAt(mixed, 1.5)), 800, 1200); // ducked under (silent) speech
    }

    [TestMethod]
    public void Mix_ChunkedStreamEqualsInMemoryMix()
    {
        var speech = new[] { ConstantWav(4000, 2.5), ConstantWav(-4000, 1.5) };
        var bed = ConstantWav(6000, 0.8);

        var inMemory = BedMixer.Mix(speech, bed, Options);
        using var stream = new MemoryStream();
        BedMixer.MixToStream(speech, bed, stream, Options);

        Assert.Equal(inMemory, stream.ToArray());
    }

    [TestMethod]
    public void AdaptToLayout_PassesThroughMatchingFormats()
    {
        var bed = WavFile.ParsePcm16Audio(ConstantWav(1000, 1.0));
        Assert.True(ReferenceEquals(bed, BedMixer.AdaptToLayout(bed, Rate, 1)));
    }

    [TestMethod]
    public void Mix_RejectsMismatchedSpeechFormats()
    {
        var speech = new[] { ConstantWav(1000, 1.0, rate: 8000), ConstantWav(1000, 1.0, rate: 16000) };
        Assert.Throws<InvalidDataException>(() => BedMixer.Mix(speech, ConstantWav(1000, 1.0)));
    }
}
