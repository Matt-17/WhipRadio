using System.Buffers.Binary;
using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ConversationRendererTests
{
    [TestMethod]
    public void SequentialRender_MatchesConcatAssemblerOutput()
    {
        var turns = new List<ConversationTurnAudio>
        {
            new(ToneWav(8000, 1, frames: 4000, amplitude: 2000), PauseAfterMs: 300),
            new(ToneWav(8000, 1, frames: 2000, amplitude: -1500)),
            new(ToneWav(8000, 1, frames: 3000, amplitude: 900)),
        };

        var rendered = ConversationRenderer.Render(turns);
        var concatenated = ConversationAssembler.Assemble(turns);

        Assert.Equal(concatenated.Length, rendered.Length);
        // Sequential slots over MixerCore must reproduce plain concatenation
        // byte for byte (same samples, same zeroed gaps).
        Assert.True(rendered.AsSpan(44).SequenceEqual(concatenated.AsSpan(44)));
    }

    [TestMethod]
    public void PauseAfterMs_ControlsTheGapBetweenTurns()
    {
        var noPause = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000), PauseAfterMs: 0),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000)),
        ]);
        var onePause = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000), PauseAfterMs: 1000),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000)),
        ]);

        var gapBytes = onePause.Length - noPause.Length;
        Assert.Equal(8000 * 2, gapBytes); // one second of mono 16-bit at 8 kHz
    }

    [TestMethod]
    public void MixedSampleRates_AdaptToTheFirstTurnsLayout()
    {
        var rendered = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(16000, 1, 1600, 1200), PauseAfterMs: 0),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1200)),
        ]);

        // Second turn resamples 8k -> 16k: 1600 + 1600 frames at 16 kHz mono.
        Assert.Equal(0.2, WavFile.GetDurationSeconds(rendered), 3);
    }

    [TestMethod]
    public void Overlap_ShortensTheTotalAndSumsBothVoices()
    {
        var turns = new List<ConversationTurnAudio>
        {
            new(ToneWav(8000, 1, 1600, 1000), PauseAfterMs: 0),
            new(ToneWav(8000, 1, 1600, 1000)),
        };

        var sequential = ConversationRenderer.Render(turns, overlapMs: 0);
        var overlapped = ConversationRenderer.Render(turns, overlapMs: 100);

        var overlapBytes = sequential.Length - overlapped.Length;
        Assert.Equal(800 * 2, overlapBytes); // 100 ms at 8 kHz mono

        // In the overlap window both tones sum: 1000 + 1000 = 2000.
        var pcm = overlapped.AsSpan(44);
        var overlapStartFrame = 1600 - 800;
        var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[(overlapStartFrame * 2)..]);
        Assert.Equal(2000, sample);
    }

    [TestMethod]
    public void NegativePauseAfterMs_OverlapsTheNextTurnByExactlyThatAmount()
    {
        var turns = new List<ConversationTurnAudio>
        {
            new(ToneWav(8000, 1, 1600, 1000), PauseAfterMs: -100),
            new(ToneWav(8000, 1, 1600, 1000)),
        };

        var sequentialLength = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 1600, 1000), PauseAfterMs: 0),
            new ConversationTurnAudio(ToneWav(8000, 1, 1600, 1000)),
        ]).Length;
        var overlapped = ConversationRenderer.Render(turns);

        Assert.Equal(800 * 2, sequentialLength - overlapped.Length); // 100 ms at 8 kHz mono

        // In the overlap window both tones sum: 1000 + 1000 = 2000.
        var pcm = overlapped.AsSpan(44);
        var overlapStartFrame = 1600 - 800;
        var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[(overlapStartFrame * 2)..]);
        Assert.Equal(2000, sample);
    }

    [TestMethod]
    public void NegativePauseAfterMs_ClampsToMaxOverlap()
    {
        // 5000 ms requested overlap on a 3200 ms first turn: the MaxOverlapMs cap
        // (1200 ms) binds before the half-turn guard (1600 ms).
        var clamped = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 25600, 1000), PauseAfterMs: -5000),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000)),
        ]);
        var atCap = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 25600, 1000), PauseAfterMs: -ConversationRenderer.MaxOverlapMs),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000)),
        ]);

        Assert.Equal(atCap.Length, clamped.Length);
    }

    [TestMethod]
    public void NegativePauseAfterMs_NeverSwallowsMoreThanHalfTheTurn()
    {
        // First turn is only 100 ms (800 frames); a 1200 ms overlap request must
        // clamp to 50 ms (400 frames), so the second turn starts mid-first-turn.
        var rendered = ConversationRenderer.Render(
        [
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000), PauseAfterMs: -ConversationRenderer.MaxOverlapMs),
            new ConversationTurnAudio(ToneWav(8000, 1, 800, 1000)),
        ]);

        // 800 + 800 frames total minus 400 overlapping frames.
        Assert.Equal((800 + 800 - 400) * 2, rendered.Length - 44);
    }

    [TestMethod]
    public void NonOverlappingSpeech_NeverClips()
    {
        var turns = new List<ConversationTurnAudio>
        {
            new(ToneWav(8000, 1, 1600, short.MaxValue), PauseAfterMs: 100),
            new(ToneWav(8000, 1, 1600, short.MinValue)),
        };

        var rendered = ConversationRenderer.Render(turns);
        var pcm = rendered.AsSpan(44);

        // Full-scale sequential turns survive untouched (no summing, no clamp distortion).
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(pcm));
        var secondTurnStart = (1600 + 800) * 2;
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(pcm[secondTurnStart..]));
    }

    private static byte[] ToneWav(int sampleRate, short channels, int frames, short amplitude)
    {
        var pcm = new byte[frames * channels * 2];
        for (var i = 0; i < frames * channels; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), amplitude);
        }

        return WavFile.WrapPcm16(pcm, sampleRate, channels);
    }
}
