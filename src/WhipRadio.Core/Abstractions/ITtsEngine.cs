namespace WhipRadio.Core.Abstractions;

public sealed record TtsResult(byte[] WavData, double DurationSeconds);

/// <summary>Instruction is a natural-language delivery hint ("warm, slightly
/// excited, brisk") for engines that support it (Qwen); others ignore it.
/// Markers remain the portable baseline for hard timing.</summary>
public sealed record TtsVoiceOptions(
    string VoiceId, string Language, double Rate, string Engine = "kokoro", string? Instruction = null);

public sealed record TtsVoice(string Id, string Language, string Gender);

public interface ITtsEngine
{
    Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct);

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct);
}
