namespace WhipRadio.Core.Audio;

/// <summary>
/// Sample-accurate summation of scheduled sources: pure math on buffers, no
/// I/O. The caller owns all buffers (zero allocations per frame) and the
/// master clock; this class applies envelopes, makeup gain, the hard master
/// clamp with clip counting, and underrun zero-fill.
/// </summary>
public sealed class MixerCore(PcmFormat format)
{
    public PcmFormat Format { get; } = format;

    /// <summary>Samples hard-clamped since the last counter reset.</summary>
    public int ClipCount { get; private set; }

    /// <summary>Mid-stream short reads since the last counter reset.</summary>
    public int UnderrunCount { get; private set; }

    public void ResetCounters()
    {
        ClipCount = 0;
        UnderrunCount = 0;
    }

    /// <summary>
    /// Mixes one frame starting at master sample <paramref name="masterPos"/>
    /// (per-channel position). Buffer lengths must be FrameSamples × Channels.
    /// </summary>
    public void MixFrame(
        long masterPos,
        IReadOnlyList<SourceSlot> slots,
        Span<short> output,
        Span<float> accumulator,
        Span<short> readScratch)
    {
        var channels = Format.Channels;
        var frameSamples = output.Length / channels;
        accumulator.Clear();

        for (var s = 0; s < slots.Count; s++)
        {
            var slot = slots[s];
            if (slot.Finished)
            {
                continue;
            }

            // Frame-relative window in which this slot is audible.
            var startInFrame = slot.StartAtMasterSample <= masterPos
                ? 0
                : (int)Math.Min(frameSamples, slot.StartAtMasterSample - masterPos);
            if (startInFrame >= frameSamples)
            {
                continue; // starts after this frame
            }

            var wanted = (frameSamples - startInFrame) * channels;
            var read = slot.Reader.Read(readScratch[..wanted]);

            if (read < wanted)
            {
                readScratch.Slice(read, wanted - read).Clear();
                if (slot.Reader.EndOfStream)
                {
                    slot.MarkFinished();
                }
                else if (read > 0 || !slot.Reader.EndOfStream)
                {
                    UnderrunCount++;
                }

                if (read == 0 && slot.Reader.EndOfStream)
                {
                    continue; // nothing audible from this slot anymore
                }
            }

            var makeup = slot.MakeupGainLinear;
            for (var i = 0; i < frameSamples - startInFrame; i++)
            {
                var gain = slot.Envelope.GainAt(masterPos + startInFrame + i) * makeup;
                if (gain == 0f)
                {
                    continue;
                }

                var frameBase = (startInFrame + i) * channels;
                for (var c = 0; c < channels; c++)
                {
                    accumulator[frameBase + c] += readScratch[i * channels + c] * gain;
                }
            }
        }

        // Master stage: hard clamp with clip counter. Loudness normalization to
        // −16 LUFS leaves ~6 dB headroom, so clips should be ~0 — the counter
        // proves it (logged per transition).
        for (var i = 0; i < output.Length; i++)
        {
            var v = accumulator[i];
            if (v > short.MaxValue)
            {
                output[i] = short.MaxValue;
                ClipCount++;
            }
            else if (v < short.MinValue)
            {
                output[i] = short.MinValue;
                ClipCount++;
            }
            else
            {
                output[i] = (short)v;
            }
        }
    }
}
