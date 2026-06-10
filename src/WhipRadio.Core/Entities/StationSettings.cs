namespace WhipRadio.Core.Entities;

/// <summary>Single-row station configuration (Id = 1).</summary>
public class StationSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string StationName { get; set; } = "WhipRadio";

    public string DefaultLanguage { get; set; } = "en";

    /// <summary>How many unplayed tracks the music producer keeps in stock.</summary>
    public int TargetQueueLength { get; set; } = 3;

    public int AnnouncementEveryNTracks { get; set; } = 1;

    // --- Production pacing -----------------------------------------------------

    /// <summary>Master switch for the music producer (admin page).</summary>
    public bool MusicProductionEnabled { get; set; } = true;

    /// <summary>Master switch for the playout/on-air engine (admin page).</summary>
    public bool PlayoutEnabled { get; set; } = true;

    /// <summary>The producer stops once this many non-retired tracks exist.</summary>
    public int MaxLibrarySize { get; set; } = 60;

    /// <summary>Generated track length range (songs are usually 3–7 minutes).</summary>
    public int MinTrackDurationSeconds { get; set; } = 180;

    public int MaxTrackDurationSeconds { get; set; } = 300;

    // --- Speech ------------------------------------------------------------------

    /// <summary>[breath] markers sound bad on some engines — off by default.</summary>
    public bool EnableBreathMarkers { get; set; }

    // --- Branding ----------------------------------------------------------------

    /// <summary>Display frequency shown in the masthead.</summary>
    public double FrequencyMhz { get; set; } = 104.4;

    /// <summary>0 = Sunday, 1 = Monday — first column of the schedule grid.</summary>
    public int FirstDayOfWeek { get; set; } = 1;

    // --- AI providers --------------------------------------------------------------

    /// <summary>"ollama" (local) | "openai" — used for scripts, titles, director reasoning.</summary>
    public string TextProvider { get; set; } = "ollama";

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public bool ElevenLabsEnabled { get; set; }

    public string ElevenLabsApiKey { get; set; } = string.Empty;

    // --- Listener interaction --------------------------------------------------

    public bool GreetingsEnabled { get; set; } = true;

    public int MaxPendingGreetings { get; set; } = 10;
}

public static class TextProviders
{
    public const string Ollama = "ollama";
    public const string OpenAi = "openai";
}

/// <summary>
/// Languages the station can broadcast in (limited by local TTS voice support).
/// The station language is the main language: hosts and all spoken texts follow it.
/// </summary>
public static class StationLanguages
{
    public static readonly IReadOnlyList<string> All = ["de", "en"];

    public static string Normalize(string? language)
        => All.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "de";
}

public static class TtsEngines
{
    public const string Kokoro = "kokoro";
    public const string Piper = "piper";
    public const string ElevenLabs = "elevenlabs";
}
