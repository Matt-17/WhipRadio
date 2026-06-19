namespace WhipRadio.Core.Abstractions;

public sealed record MusicRequest(
    string Prompt,
    string Genre,
    bool WantVocals,
    string? Lyrics,
    int DurationSeconds)
{
    public string? Provider { get; init; }

    public string? SubGenre { get; init; }

    public string? Style { get; init; }

    public LyricsMode LyricsMode { get; init; } = WantVocals
        ? LyricsMode.Provided
        : LyricsMode.Instrumental;

    public string? Language { get; init; }

    public VocalGender VocalGender { get; init; } = VocalGender.Unspecified;

    public string? VocalStyle { get; init; }

    public int? Bpm { get; init; }

    public string? KeyScale { get; init; }

    public string? TimeSignature { get; init; }

    public int? Seed { get; init; }

    public string? ArtistName { get; init; }

    public string? ArtistBackstory { get; init; }

    public string? ArtistStyleDescription { get; init; }

    public string? SongTitle { get; init; }

    public string? SongStory { get; init; }

    public string? ArtistSongHistory { get; init; }

    public bool AllowProviderFallback { get; init; } = true;

    public Func<MusicGenerationProgress, CancellationToken, ValueTask>? ProgressReporter { get; init; }
}

public sealed record MusicGenerationProgress(
    string? TaskId,
    string Message,
    double? Percent = null);

public enum LyricsMode
{
    Instrumental,
    Auto,
    Provided,
}

public enum VocalGender
{
    Unspecified,
    Male,
    Female,
    Mixed,
}

public sealed record MusicResult(
    byte[] WavData,
    string BackendUsed,
    string? ModelUsed = null,
    string? SeedUsed = null,
    string? TaskId = null);

public static class MusicBackends
{
    public const string MusicGen = "musicgen";
    public const string AceStep = "ace-step-1.5";
    public const string AceStepAlias = "ace-step";
    public const string ElevenLabs = "elevenlabs";

    public static string Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return MusicGen;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            MusicGen => MusicGen,
            AceStep or AceStepAlias => AceStep,
            var value => value,
        };
    }

    public static bool IsKnown(string? provider)
    {
        var normalized = Normalize(provider);
        return normalized is MusicGen or AceStep or ElevenLabs;
    }
}

public interface IMusicGenerator
{
    Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct);

    Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct);
}
