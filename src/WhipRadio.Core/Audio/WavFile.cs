using System.Buffers.Binary;

namespace WhipRadio.Core.Audio;

/// <summary>Parsed 16-bit PCM payload of a WAV file (see <see cref="WavFile.ParsePcm16Audio"/>).</summary>
public sealed record Pcm16Audio(short Channels, int SampleRate, ReadOnlyMemory<byte> Data)
{
    public int BytesPerFrame => Channels * 2;

    public long FrameCount => Data.Length / BytesPerFrame;

    public double DurationSeconds => FrameCount / (double)SampleRate;
}

/// <summary>Tiny RIFF/WAVE header reader — enough to compute a duration without decoding.</summary>
public static class WavFile
{
    private sealed record PcmLayout(short Channels, int SampleRate, short BitsPerSample, ReadOnlyMemory<byte> Data)
    {
        public int BytesPerSampleFrame => Channels * (BitsPerSample / 8);
    }

    /// <summary>Parses a 16-bit PCM WAV into its raw sample payload (offline mixing).</summary>
    /// <exception cref="InvalidDataException">Not a parsable 16-bit PCM WAV file.</exception>
    public static Pcm16Audio ParsePcm16Audio(byte[] wav)
    {
        var layout = ParsePcm16(wav);
        return new Pcm16Audio(layout.Channels, layout.SampleRate, layout.Data);
    }

    /// <summary>Computes the duration from the fmt byte rate and the data chunk size.</summary>
    /// <exception cref="InvalidDataException">Not a parsable WAV file.</exception>
    public static double GetDurationSeconds(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 ||
            !wav[..4].SequenceEqual("RIFF"u8) ||
            !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        uint byteRate = 0;
        long dataLength = -1;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = wav.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));

            if (chunkId.SequenceEqual("fmt "u8) && offset + 8 + 16 <= wav.Length)
            {
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 8 + 8, 4));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                // Streamed writers sometimes leave the size as 0/0xFFFFFFFF; fall back to the actual payload.
                dataLength = chunkSize is 0 or uint.MaxValue
                    ? wav.Length - (offset + 8)
                    : Math.Min(chunkSize, wav.Length - (offset + 8));
            }

            offset += 8 + (int)chunkSize + ((int)chunkSize & 1); // chunks are word-aligned
            if (chunkSize is 0 or uint.MaxValue)
            {
                break;
            }
        }

        if (byteRate == 0 || dataLength < 0)
        {
            throw new InvalidDataException("WAV file is missing fmt or data chunk.");
        }

        return dataLength / (double)byteRate;
    }

    /// <summary>Wraps raw 16-bit PCM samples in a minimal WAV container.</summary>
    public static byte[] WrapPcm16(ReadOnlySpan<byte> pcm, int sampleRate, short channels)
    {
        var byteRate = sampleRate * channels * 2;
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + pcm.Length));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
        pcm.CopyTo(span[44..]);

        return wav;
    }

    /// <summary>Concatenates matching PCM WAV files without decoding or resampling.</summary>
    public static byte[] ConcatPcm16(IReadOnlyList<byte[]> wavFiles)
    {
        if (wavFiles.Count == 0)
        {
            throw new ArgumentException("At least one WAV file is required.", nameof(wavFiles));
        }

        var layouts = wavFiles.Select(ParsePcm16).ToList();
        var first = layouts[0];
        if (layouts.Any(layout => layout.Channels != first.Channels
            || layout.SampleRate != first.SampleRate
            || layout.BitsPerSample != first.BitsPerSample))
        {
            throw new InvalidDataException("Cannot concatenate WAV files with different PCM formats.");
        }

        var totalBytes = layouts.Sum(layout => layout.Data.Length);
        var pcm = new byte[totalBytes];
        var offset = 0;
        foreach (var layout in layouts)
        {
            layout.Data.Span.CopyTo(pcm.AsSpan(offset));
            offset += layout.Data.Length;
        }

        return WrapPcm16(pcm, first.SampleRate, first.Channels);
    }

    /// <summary>Copies a time slice from a 16-bit PCM WAV file without decoding or resampling.</summary>
    public static byte[] SlicePcm16(byte[] wav, double startSeconds, double durationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        var layout = ParsePcm16(wav);
        var frameBytes = layout.BytesPerSampleFrame;
        var totalFrames = layout.Data.Length / frameBytes;
        var startFrame = Math.Clamp((long)Math.Floor(startSeconds * layout.SampleRate), 0, totalFrames);
        var requestedFrames = Math.Max(1, (long)Math.Ceiling(durationSeconds * layout.SampleRate));
        var frames = Math.Min(requestedFrames, totalFrames - startFrame);
        if (frames <= 0)
        {
            throw new InvalidDataException("Requested WAV slice starts after the data chunk.");
        }

        var byteOffset = checked((int)(startFrame * frameBytes));
        var byteCount = checked((int)(frames * frameBytes));
        return WrapPcm16(layout.Data.Span.Slice(byteOffset, byteCount), layout.SampleRate, layout.Channels);
    }

    private static PcmLayout ParsePcm16(byte[] wav)
    {
        if (wav.Length < 12 ||
            !wav.AsSpan()[..4].SequenceEqual("RIFF"u8) ||
            !wav.AsSpan()[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        short audioFormat = 0;
        short channels = 0;
        var sampleRate = 0;
        short bitsPerSample = 0;
        ReadOnlyMemory<byte>? data = null;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var span = wav.AsSpan(offset);
            var chunkId = span[..4];
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
            var payloadOffset = offset + 8;
            var available = wav.Length - payloadOffset;
            var payloadLength = chunkSize is 0 or uint.MaxValue
                ? available
                : Math.Min((int)chunkSize, available);

            if (chunkId.SequenceEqual("fmt "u8) && payloadLength >= 16)
            {
                var payload = wav.AsSpan(payloadOffset, payloadLength);
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(payload);
                channels = BinaryPrimitives.ReadInt16LittleEndian(payload[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(payload[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = wav.AsMemory(payloadOffset, payloadLength);
            }

            offset += 8 + (int)chunkSize + ((int)chunkSize & 1);
            if (chunkSize is 0 or uint.MaxValue)
            {
                break;
            }
        }

        if (audioFormat != 1 || channels <= 0 || sampleRate <= 0 || bitsPerSample != 16 || data is null)
        {
            throw new InvalidDataException("WAV file is not 16-bit PCM or is missing fmt/data chunks.");
        }

        var layout = new PcmLayout(channels, sampleRate, bitsPerSample, data.Value);
        if (layout.Data.Length % layout.BytesPerSampleFrame != 0)
        {
            throw new InvalidDataException("WAV data chunk is not aligned to the PCM frame size.");
        }

        return layout;
    }
}
