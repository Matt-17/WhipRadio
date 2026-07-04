using System.Buffers.Binary;
using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ConversationAssemblerTests
{
    private const int Rate = 8000;

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

    [TestMethod]
    public void Assemble_InsertsDefaultGapsBetweenTurnsOnly()
    {
        var result = ConversationAssembler.Assemble(
        [
            new ConversationTurnAudio(ConstantWav(1000, 1.0)),
            new ConversationTurnAudio(ConstantWav(2000, 1.0)),
            new ConversationTurnAudio(ConstantWav(3000, 1.0)),
        ]);

        // 3 × 1.0 s speech + 2 × 0.4 s gaps = 3.8 s; no trailing gap.
        Assert.Equal(3.8, WavFile.GetDurationSeconds(result), precision: 2);
        Assert.Equal((short)1000, SampleAt(result, 0.5));
        Assert.Equal((short)0, SampleAt(result, 1.2));     // inside the first gap
        Assert.Equal((short)2000, SampleAt(result, 1.9));  // second turn after the gap
        Assert.Equal((short)3000, SampleAt(result, 3.5));
    }

    [TestMethod]
    public void Assemble_HonorsPerTurnPauseHints()
    {
        var result = ConversationAssembler.Assemble(
        [
            new ConversationTurnAudio(ConstantWav(1000, 1.0), PauseAfterMs: 1000),
            new ConversationTurnAudio(ConstantWav(2000, 1.0)),
        ]);

        Assert.Equal(3.0, WavFile.GetDurationSeconds(result), precision: 2);
        Assert.Equal((short)0, SampleAt(result, 1.5));
        Assert.Equal((short)2000, SampleAt(result, 2.5));
    }

    [TestMethod]
    public void Assemble_AdaptsMismatchedLayoutsInsteadOfThrowing()
    {
        var result = ConversationAssembler.Assemble(
        [
            new ConversationTurnAudio(ConstantWav(1000, 1.0)),                        // 8 kHz mono
            new ConversationTurnAudio(ConstantWav(2000, 1.0, rate: 16000, channels: 2)), // 16 kHz stereo
        ]);

        var audio = WavFile.ParsePcm16Audio(result);
        Assert.Equal(Rate, audio.SampleRate);
        Assert.Equal((short)1, audio.Channels);
        Assert.Equal(2.4, WavFile.GetDurationSeconds(result), precision: 2);
        Assert.InRange(SampleAt(result, 2.0), 1900, 2100); // adapted second turn audible
    }

    [TestMethod]
    public void Assemble_SingleTurn_PassesThroughWithoutGaps()
    {
        var single = ConstantWav(1234, 2.0);
        var result = ConversationAssembler.Assemble([new ConversationTurnAudio(single)]);
        Assert.Equal(2.0, WavFile.GetDurationSeconds(result), precision: 2);
        Assert.Equal((short)1234, SampleAt(result, 1.0));
    }

    [TestMethod]
    public void Assemble_ClampsOversizedPauseHints()
    {
        var result = ConversationAssembler.Assemble(
        [
            new ConversationTurnAudio(ConstantWav(1000, 0.5), PauseAfterMs: 60_000),
            new ConversationTurnAudio(ConstantWav(2000, 0.5)),
        ]);

        Assert.Equal(0.5 + 3.0 + 0.5, WavFile.GetDurationSeconds(result), precision: 2);
    }
}
