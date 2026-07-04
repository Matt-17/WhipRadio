using System.Runtime.InteropServices;

namespace WhipRadio.Core.Audio;

/// <summary>
/// Offline conversation premix over the pure <see cref="MixerCore"/> (Phase 5,
/// firm per Phase 0): every turn becomes a <see cref="SourceSlot"/> scheduled
/// on a master sample clock, so turn timing is deterministic and cross-talk is
/// just a parameter — <paramref name="overlapMs"/> pulls each turn into the
/// tail of the previous one. The first cut keeps turns sequential
/// (overlap 0), matching <see cref="ConversationAssembler"/>'s output. The
/// live mixer is not involved; the result is one WAV.
/// </summary>
public static class ConversationRenderer
{
    public const int DefaultGapMs = ConversationAssembler.DefaultGapMs;
    public const int MaxGapMs = ConversationAssembler.MaxGapMs;

    public static byte[] Render(
        IReadOnlyList<ConversationTurnAudio> turns,
        int defaultGapMs = DefaultGapMs,
        int overlapMs = 0)
    {
        ArgumentOutOfRangeException.ThrowIfZero(turns.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapMs);

        var parsed = turns
            .Select(turn => WavFile.ParsePcm16Audio(turn.Wav))
            .ToList();
        var layout = parsed[0];
        for (var i = 1; i < parsed.Count; i++)
        {
            parsed[i] = BedMixer.AdaptToLayout(parsed[i], layout.SampleRate, layout.Channels);
        }

        var format = new PcmFormat(layout.SampleRate, layout.Channels);
        var slots = new List<SourceSlot>(parsed.Count);
        long cursor = 0; // master position (samples per channel)
        long totalFrames = 0;
        for (var i = 0; i < parsed.Count; i++)
        {
            var startAt = cursor;
            slots.Add(new SourceSlot
            {
                Reader = new MemoryPcmSampleReader(parsed[i].Data),
                Envelope = new GainEnvelope(),
                StartAtMasterSample = startAt,
            });

            var frames = parsed[i].FrameCount;
            totalFrames = Math.Max(totalFrames, startAt + frames);
            if (i < parsed.Count - 1)
            {
                var gapMs = Math.Clamp(turns[i].PauseAfterMs ?? defaultGapMs, 0, MaxGapMs) - overlapMs;
                var gapFrames = (long)Math.Round(gapMs / 1000.0 * layout.SampleRate);
                // Overlap may pull the next turn into this one's tail, but never
                // before this turn's start.
                cursor = Math.Max(startAt, startAt + frames + gapFrames);
            }
        }

        var channels = layout.Channels;
        var output = new short[PcmFormat.FrameSamples * channels];
        var accumulator = new float[output.Length];
        var readScratch = new short[output.Length];
        var mixer = new MixerCore(format);

        var pcm = new byte[totalFrames * layout.BytesPerFrame];
        long masterPos = 0;
        long bytesWritten = 0;
        while (masterPos < totalFrames)
        {
            var frameSamples = (int)Math.Min(PcmFormat.FrameSamples, totalFrames - masterPos);
            var span = output.AsSpan(0, frameSamples * channels);
            mixer.MixFrame(
                masterPos,
                slots,
                span,
                accumulator.AsSpan(0, span.Length),
                readScratch.AsSpan(0, span.Length));
            MemoryMarshal.AsBytes(span).CopyTo(pcm.AsSpan((int)bytesWritten));
            bytesWritten += span.Length * 2;
            masterPos += frameSamples;
        }

        return WavFile.WrapPcm16(pcm, layout.SampleRate, layout.Channels);
    }

    /// <summary>Pull-reader over an in-memory 16-bit PCM payload (little-endian).</summary>
    private sealed class MemoryPcmSampleReader(ReadOnlyMemory<byte> pcm) : IPcmSampleReader
    {
        private int _position; // bytes consumed

        public bool EndOfStream => _position >= pcm.Length;

        public int Read(Span<short> frame)
        {
            var remainingSamples = (pcm.Length - _position) / 2;
            var samples = Math.Min(frame.Length, remainingSamples);
            if (samples <= 0)
            {
                return 0;
            }

            var bytes = pcm.Span.Slice(_position, samples * 2);
            bytes.CopyTo(MemoryMarshal.AsBytes(frame[..samples]));
            _position += samples * 2;
            return samples;
        }
    }
}
