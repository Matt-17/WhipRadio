namespace WhipRadio.Core.Abstractions;

public sealed record TtsResult(byte[] WavData, double DurationSeconds);

/// <summary>Instruction is a natural-language delivery hint ("warm, slightly
/// excited, brisk") for engines that support it (Qwen); others ignore it.
/// Markers remain the portable baseline for hard timing.
/// <para>
/// <paramref name="Operation"/> (e.g. "news intro") and <paramref name="SpeakerName"/> are
/// presentation-only: they label the recording in the studio history / Writers Room. The
/// engine ignores them.
/// </para></summary>
public sealed record TtsVoiceOptions(
    string VoiceId,
    string Language,
    double Rate,
    string Engine = "kokoro",
    string? Instruction = null,
    string? Operation = null,
    string? SpeakerName = null);

public sealed record TtsVoice(string Id, string Language, string Gender);

public interface ITtsEngine
{
    Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct);

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct);
}
