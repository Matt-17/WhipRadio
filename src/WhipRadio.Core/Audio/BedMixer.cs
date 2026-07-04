using System.Buffers.Binary;

namespace WhipRadio.Core.Audio;

/// <summary>Levels and timing for mixing a music bed under a spoken block.</summary>
public sealed record BedMixOptions(
    double BedLevelDbUnderSpeech = -14,
    double GapBetweenPartsSeconds = 2.0,
    double LeadSeconds = 1.5,
    double TailSeconds = 2.5,
    int DuckRampMs = 600,
    double FadeOutSeconds = 1.5)
{
    public static BedMixOptions Default { get; } = new();
}

/// <summary>
/// Offline compositor for the long news show: lays the spoken chapters out on a
/// timeline (short bed-only gaps between them), loops one instrumental bed across
/// the whole block, ducks it under speech, and lets it breathe at full level in
/// the lead/gaps/tail. Audio is written chunk-wise to the output stream so a
/// 30-minute composite never holds the full mix in memory. The bed is adapted to
/// the speech PCM layout (channel downmix + linear resample) — beds come from the
/// music backend and rarely match the TTS format.
/// </summary>
public static class BedMixer
{
    private const int ChunkFrames = 48_000; // ~1-2 s per write, small steady buffers

    /// <summary>Mixes into <paramref name="output"/> as a complete WAV; returns the duration.</summary>
    public static double MixToStream(
        IReadOnlyList<byte[]> speechWavs, byte[] bedWav, Stream output, BedMixOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(speechWavs.Count);
        var opts = options ?? BedMixOptions.Default;

        var parts = speechWavs.Select(WavFile.ParsePcm16Audio).ToList();
        var first = parts[0];
        if (parts.Any(part => part.Channels != first.Channels || part.SampleRate != first.SampleRate))
        {
            throw new InvalidDataException("Cannot mix speech parts with different PCM formats.");
        }

        var bed = AdaptToLayout(WavFile.ParsePcm16Audio(bedWav), first.SampleRate, first.Channels);
        if (bed.FrameCount <= 0)
        {
            throw new InvalidDataException("The bed WAV has no audio to loop.");
        }

        var rate = first.SampleRate;
        var channels = first.Channels;
        long ToFrames(double seconds) => (long)Math.Round(Math.Max(0, seconds) * rate);

        // Timeline: lead → part → gap → part → … → tail (+ fade-out inside the tail).
        var gapFrames = ToFrames(opts.GapBetweenPartsSeconds);
        var intervals = new List<(long Start, long End, Pcm16Audio Part)>();
        var cursor = ToFrames(opts.LeadSeconds);
        foreach (var part in parts)
        {
            intervals.Add((cursor, cursor + part.FrameCount, part));
            cursor += part.FrameCount + gapFrames;
        }

        var lastSpeechEnd = intervals[^1].End;
        var totalFrames = lastSpeechEnd + ToFrames(opts.TailSeconds);
        var fadeFrames = Math.Min(ToFrames(opts.FadeOutSeconds), totalFrames);
        var rampFrames = Math.Max(1, ToFrames(opts.DuckRampMs / 1000.0));
        var duckGain = (float)TransitionMath.DbToLinear(opts.BedLevelDbUnderSpeech);

        WriteWavHeader(output, totalFrames, rate, channels);

        var bedData = bed.Data.Span;
        var bytesPerFrame = first.BytesPerFrame;
        var samplesPerFrame = channels;
        var buffer = new byte[ChunkFrames * bytesPerFrame];
        var intervalIndex = 0;

        for (long frame = 0; frame < totalFrames;)
        {
            var chunk = (int)Math.Min(ChunkFrames, totalFrames - frame);
            var span = buffer.AsSpan(0, chunk * bytesPerFrame);
            span.Clear();

            for (var i = 0; i < chunk; i++)
            {
                var t = frame + i;
                while (intervalIndex < intervals.Count && t >= intervals[intervalIndex].End)
                {
                    intervalIndex++;
                }

                var gain = BedGainAt(t, intervals, intervalIndex, rampFrames, duckGain);
                if (fadeFrames > 0 && t >= totalFrames - fadeFrames)
                {
                    gain *= (float)((totalFrames - t) / (double)fadeFrames);
                }

                var bedFrame = (int)(t % bed.FrameCount);
                for (var c = 0; c < samplesPerFrame; c++)
                {
                    var bedSample = BinaryPrimitives.ReadInt16LittleEndian(
                        bedData.Slice((bedFrame * samplesPerFrame + c) * 2, 2));
                    var mixed = bedSample * gain;

                    if (intervalIndex < intervals.Count
                        && t >= intervals[intervalIndex].Start
                        && t < intervals[intervalIndex].End)
                    {
                        var (start, _, part) = intervals[intervalIndex];
                        var partSampleIndex = ((t - start) * samplesPerFrame + c) * 2;
                        mixed += BinaryPrimitives.ReadInt16LittleEndian(
                            part.Data.Span.Slice((int)partSampleIndex, 2));
                    }

                    BinaryPrimitives.WriteInt16LittleEndian(
                        span.Slice((i * samplesPerFrame + c) * 2, 2),
                        (short)Math.Clamp((int)Math.Round(mixed), short.MinValue, short.MaxValue));
                }
            }

            output.Write(span);
            frame += chunk;
        }

        output.Flush();
        return totalFrames / (double)rate;
    }

    /// <summary>Convenience for tests/small blocks: mixes fully in memory.</summary>
    public static byte[] Mix(IReadOnlyList<byte[]> speechWavs, byte[] bedWav, BedMixOptions? options = null)
    {
        using var stream = new MemoryStream();
        MixToStream(speechWavs, bedWav, stream, options);
        return stream.ToArray();
    }

    /// <summary>
    /// Bed gain at one frame: ducked under speech, full in the open, linear ramps
    /// completing at the part edges (down before speech starts, up after it ends).
    /// </summary>
    private static float BedGainAt(
        long t,
        IReadOnlyList<(long Start, long End, Pcm16Audio Part)> intervals,
        int nextOrCurrentIndex,
        long rampFrames,
        float duckGain)
    {
        if (nextOrCurrentIndex < intervals.Count && t >= intervals[nextOrCurrentIndex].Start)
        {
            return duckGain; // inside speech
        }

        var gain = 1f;
        if (nextOrCurrentIndex < intervals.Count)
        {
            var untilNext = intervals[nextOrCurrentIndex].Start - t;
            if (untilNext < rampFrames)
            {
                gain = Math.Min(gain, duckGain + (1 - duckGain) * (untilNext / (float)rampFrames));
            }
        }

        if (nextOrCurrentIndex > 0)
        {
            var sincePrevious = t - intervals[nextOrCurrentIndex - 1].End;
            if (sincePrevious < rampFrames)
            {
                gain = Math.Min(gain, duckGain + (1 - duckGain) * (sincePrevious / (float)rampFrames));
            }
        }

        return gain;
    }

    /// <summary>Downmixes/duplicates channels and linearly resamples the bed to the speech layout.</summary>
    public static Pcm16Audio AdaptToLayout(Pcm16Audio bed, int targetRate, short targetChannels)
    {
        if (bed.SampleRate == targetRate && bed.Channels == targetChannels)
        {
            return bed;
        }

        var sourceFrames = bed.FrameCount;
        var targetFrames = (long)Math.Round(sourceFrames * (double)targetRate / bed.SampleRate);
        var result = new byte[targetFrames * targetChannels * 2];
        var source = bed.Data.Span;

        for (long frame = 0; frame < targetFrames; frame++)
        {
            var position = frame * (double)bed.SampleRate / targetRate;
            var index = Math.Min((long)position, sourceFrames - 1);
            var next = Math.Min(index + 1, sourceFrames - 1);
            var blend = position - index;

            // Downmix the source frame to mono first, then spread to the target channels.
            var sample = (short)Math.Clamp(
                (int)Math.Round(MonoFrame(source, index, bed.Channels) * (1 - blend)
                    + MonoFrame(source, next, bed.Channels) * blend),
                short.MinValue,
                short.MaxValue);
            for (var c = 0; c < targetChannels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    result.AsSpan((int)((frame * targetChannels + c) * 2), 2), sample);
            }
        }

        return new Pcm16Audio(targetChannels, targetRate, result);
    }

    private static double MonoFrame(ReadOnlySpan<byte> source, long frame, short channels)
    {
        double sum = 0;
        for (var c = 0; c < channels; c++)
        {
            sum += BinaryPrimitives.ReadInt16LittleEndian(source.Slice((int)((frame * channels + c) * 2), 2));
        }

        return sum / channels;
    }

    private static void WriteWavHeader(Stream output, long totalFrames, int sampleRate, short channels)
    {
        var dataBytes = totalFrames * channels * 2;
        if (dataBytes > uint.MaxValue - 36)
        {
            throw new InvalidDataException("Composite exceeds the WAV size limit.");
        }

        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        output.Write(header);
    }
}
