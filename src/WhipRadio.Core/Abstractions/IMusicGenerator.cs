namespace WhipRadio.Core.Abstractions;

public sealed record MusicRequest(
    string Prompt,
    string Genre,
    bool WantVocals,
    string? Lyrics,
    int DurationSeconds);

public sealed record MusicResult(byte[] WavData, string BackendUsed);

public static class MusicBackends
{
    public const string MusicGen = "musicgen";
    public const string AceStep = "ace-step";
}

public interface IMusicGenerator
{
    Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct);

    Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct);
}
