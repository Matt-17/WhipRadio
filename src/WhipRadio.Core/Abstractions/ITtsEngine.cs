namespace WhipRadio.Core.Abstractions;

public sealed record TtsResult(byte[] WavData, double DurationSeconds);

public sealed record TtsVoiceOptions(string VoiceId, string Language, double Rate);

public sealed record TtsVoice(string Id, string Language, string Gender);

public interface ITtsEngine
{
    Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct);

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct);
}
