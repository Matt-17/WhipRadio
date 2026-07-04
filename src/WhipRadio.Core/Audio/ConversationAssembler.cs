namespace WhipRadio.Core.Audio;

/// <summary>One voiced turn ready for assembly, with its trailing-gap timing hint.</summary>
public sealed record ConversationTurnAudio(byte[] Wav, int? PauseAfterMs = null);

/// <summary>
/// Assembles a multi-speaker conversation into one WAV: the ordered turn
/// recordings separated by short silence gaps (the brief's v1 "simple ordered
/// turn spacing" — no beds or crossfades). Turns from different TTS engines are
/// adapted to the first turn's PCM layout (channel downmix + linear resample)
/// instead of failing on a format mismatch.
/// </summary>
public static class ConversationAssembler
{
    public const int DefaultGapMs = 400;
    public const int MaxGapMs = 3000;

    public static byte[] Assemble(IReadOnlyList<ConversationTurnAudio> turns, int defaultGapMs = DefaultGapMs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(turns.Count);

        var parsed = turns
            .Select(turn => WavFile.ParsePcm16Audio(turn.Wav))
            .ToList();
        var layout = parsed[0];
        for (var i = 1; i < parsed.Count; i++)
        {
            parsed[i] = BedMixer.AdaptToLayout(parsed[i], layout.SampleRate, layout.Channels);
        }

        var bytesPerFrame = layout.BytesPerFrame;
        long GapBytes(int? pauseMs)
        {
            var ms = Math.Clamp(pauseMs ?? defaultGapMs, 0, MaxGapMs);
            return (long)Math.Round(ms / 1000.0 * layout.SampleRate) * bytesPerFrame;
        }

        long totalBytes = parsed.Sum(audio => (long)audio.Data.Length);
        for (var i = 0; i < turns.Count - 1; i++)
        {
            totalBytes += GapBytes(turns[i].PauseAfterMs);
        }

        var pcm = new byte[totalBytes];
        var offset = 0L;
        for (var i = 0; i < parsed.Count; i++)
        {
            parsed[i].Data.Span.CopyTo(pcm.AsSpan((int)offset));
            offset += parsed[i].Data.Length;
            if (i < parsed.Count - 1)
            {
                offset += GapBytes(turns[i].PauseAfterMs); // silence = zeroed PCM
            }
        }

        return WavFile.WrapPcm16(pcm, layout.SampleRate, layout.Channels);
    }
}
