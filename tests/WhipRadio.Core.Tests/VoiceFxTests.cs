using System.Buffers.Binary;
using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

[TestClass]
public class VoiceFxTests
{
    private const int Rate = 24000;

    [TestMethod]
    public void Telephone_AttenuatesOutOfBandFrequenciesAndKeepsThePassband()
    {
        // 2nd-order filters at 300/3400 Hz: 100 Hz and 8 kHz sit well outside
        // the phone passband, 1 kHz sits in the middle of it.
        var lowRatio = RmsRatio(100);
        var midRatio = RmsRatio(1000);
        var highRatio = RmsRatio(8000);

        Assert.True(Db(lowRatio) < -12, $"100 Hz only attenuated {Db(lowRatio):F1} dB");
        Assert.True(Db(highRatio) < -12, $"8 kHz only attenuated {Db(highRatio):F1} dB");
        Assert.True(Math.Abs(Db(midRatio)) < 3, $"1 kHz changed by {Db(midRatio):F1} dB");
    }

    [TestMethod]
    public void Telephone_PreservesLengthAndLayout()
    {
        var input = SineWav(440, amplitude: 0.5, channels: 2);
        var output = VoiceFx.Apply(VoiceFx.Telephone, input);

        Assert.Equal(input.Length, output.Length);
        var parsed = WavFile.ParsePcm16Audio(output);
        Assert.Equal((short)2, parsed.Channels);
        Assert.Equal(Rate, parsed.SampleRate);
    }

    [TestMethod]
    public void UnknownEffect_IsPassthrough()
    {
        var input = SineWav(440, amplitude: 0.5);

        Assert.Same(input, VoiceFx.Apply(null, input));
        Assert.Same(input, VoiceFx.Apply("", input));
        Assert.Same(input, VoiceFx.Apply("reverb-cathedral", input));
    }

    [TestMethod]
    public void UnparsableAudio_IsPassthrough()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.Same(garbage, VoiceFx.Apply(VoiceFx.Telephone, garbage));
    }

    private static double RmsRatio(double frequencyHz)
    {
        var input = SineWav(frequencyHz, amplitude: 0.25);
        var output = VoiceFx.Apply(VoiceFx.Telephone, input);
        // Skip the first 100 ms so the filters have settled.
        return Rms(output, skipSeconds: 0.1) / Rms(input, skipSeconds: 0.1);
    }

    private static byte[] SineWav(double frequencyHz, double amplitude, short channels = 1, double seconds = 0.5)
    {
        var frames = (int)(seconds * Rate);
        var pcm = new byte[frames * channels * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequencyHz * frame / Rate) * amplitude * short.MaxValue);
            for (var channel = 0; channel < channels; channel++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2), value);
            }
        }

        return WavFile.WrapPcm16(pcm, Rate, channels);
    }

    private static double Rms(byte[] wav, double skipSeconds)
    {
        var audio = WavFile.ParsePcm16Audio(wav);
        var samples = audio.Data.Span;
        var start = (int)(skipSeconds * audio.SampleRate) * audio.BytesPerFrame;
        double sum = 0;
        var count = 0;
        for (var i = start; i + 1 < samples.Length; i += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(samples[i..]);
            sum += sample * sample;
            count++;
        }

        return Math.Sqrt(sum / Math.Max(1, count));
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);
}
