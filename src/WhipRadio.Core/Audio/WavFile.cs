using System.Buffers.Binary;

namespace WhipRadio.Core.Audio;

/// <summary>Tiny RIFF/WAVE header reader — enough to compute a duration without decoding.</summary>
public static class WavFile
{
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
}
