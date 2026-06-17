using System.Buffers.Binary;
using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class WavFileTests
{
    private static byte[] BuildWav(int dataBytes, int byteRate)
    {
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], byteRate); // sample rate (unused by parser)
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        return wav;
    }

    [TestMethod]
    public void GetDurationSeconds_ComputesFromByteRateAndDataSize()
    {
        var wav = BuildWav(dataBytes: 88200, byteRate: 88200);
        Assert.Equal(1.0, WavFile.GetDurationSeconds(wav), precision: 6);
    }

    [TestMethod]
    public void GetDurationSeconds_HalfSecond()
    {
        var wav = BuildWav(dataBytes: 44100, byteRate: 88200);
        Assert.Equal(0.5, WavFile.GetDurationSeconds(wav), precision: 6);
    }

    [TestMethod]
    public void GetDurationSeconds_NotAWavFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.GetDurationSeconds("definitely not a wav"u8.ToArray()));
    }

    [TestMethod]
    public void GetDurationSeconds_TruncatedFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.GetDurationSeconds([0x52, 0x49]));
    }
}
