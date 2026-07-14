namespace WhipRadio.Core.Audio;

/// <summary>
/// Post-TTS voice effect chains (Phase-0-Deferred §8). Pure PCM DSP over a
/// parsed WAV: the "telephone" chain band-limits the voice to the classic
/// phone-line passband (~300–3400 Hz) and adds a touch of saturation so a
/// caller guest sounds like a phone line without needing a different voice.
/// Failure-soft by design: unknown effect names and unparsable audio return
/// the input unchanged.
/// </summary>
public static class VoiceFx
{
    public const string Telephone = "telephone";

    /// <summary>All effect names the console may offer.</summary>
    public static readonly IReadOnlyList<string> KnownEffects = [Telephone];

    private const double HighPassHz = 300;
    private const double LowPassHz = 3400;
    private const double FilterQ = 0.70710678; // Butterworth
    private const float Drive = 1.5f;

    public static byte[] Apply(string? fx, byte[] wav)
    {
        if (!string.Equals(fx, Telephone, StringComparison.OrdinalIgnoreCase))
        {
            return wav;
        }

        Pcm16Audio audio;
        try
        {
            audio = WavFile.ParsePcm16Audio(wav);
        }
        catch (InvalidDataException)
        {
            return wav;
        }

        var channels = audio.Channels;
        var samples = MemoryMarshalPcm(audio.Data);
        var processed = new byte[audio.Data.Length];
        var output = processed.AsSpan();

        // Unity small-signal gain: speech RMS sits well below full scale, so
        // normalizing the linear region keeps loudness comparable while peaks
        // are softly compressed by the tanh curve.
        var makeup = 1f / Drive;
        for (var channel = 0; channel < channels; channel++)
        {
            var highPass = Biquad.HighPass(audio.SampleRate, HighPassHz, FilterQ);
            // Skip the low-pass when the cutoff would sit at/above Nyquist.
            Biquad? lowPass = LowPassHz * 2 < audio.SampleRate
                ? Biquad.LowPass(audio.SampleRate, LowPassHz, FilterQ)
                : null;

            for (var i = channel; i < samples.Length; i += channels)
            {
                var sample = samples[i] / 32768f;
                sample = highPass.Process(sample);
                if (lowPass is not null)
                {
                    sample = lowPass.Process(sample);
                }

                sample = MathF.Tanh(sample * Drive) * makeup;
                var value = (int)MathF.Round(sample * 32767f);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                    output[(i * 2)..], (short)Math.Clamp(value, short.MinValue, short.MaxValue));
            }
        }

        return WavFile.WrapPcm16(processed, audio.SampleRate, channels);
    }

    private static ReadOnlySpan<short> MemoryMarshalPcm(ReadOnlyMemory<byte> data)
        => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data.Span);

    /// <summary>RBJ cookbook biquad (direct form I), one instance per channel.</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
        }

        public static Biquad HighPass(int sampleRate, double cutoffHz, double q)
        {
            var w0 = 2 * Math.PI * cutoffHz / sampleRate;
            var (sin, cos) = Math.SinCos(w0);
            var alpha = sin / (2 * q);
            return new Biquad(
                (1 + cos) / 2, -(1 + cos), (1 + cos) / 2,
                1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad LowPass(int sampleRate, double cutoffHz, double q)
        {
            var w0 = 2 * Math.PI * cutoffHz / sampleRate;
            var (sin, cos) = Math.SinCos(w0);
            var alpha = sin / (2 * q);
            return new Biquad(
                (1 - cos) / 2, 1 - cos, (1 - cos) / 2,
                1 + alpha, -2 * cos, 1 - alpha);
        }

        public float Process(float x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
