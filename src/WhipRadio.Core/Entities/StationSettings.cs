using WhipRadio.Core.Abstractions;

namespace WhipRadio.Core.Entities;

/// <summary>Single-row station configuration (Id = 1).</summary>
public class StationSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string StationName { get; set; } = "WhipRadio";

    public string DefaultLanguage { get; set; } = "en";

    public string DefaultMusicProvider { get; set; } = MusicBackends.MusicGen;

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

    // --- Mixer (Phase 3a) — hot-reloadable, read once per transition -------------

    /// <summary>Master flag for the real-time mixer; off = legacy sequential playout.</summary>
    public bool MixerEnabled { get; set; }

    /// <summary>Loudness normalization target (EBU R128 integrated).</summary>
    public double TargetLufs { get; set; } = -16.0;

    /// <summary>Clamp for makeup gain on quiet items.</summary>
    public double MaxMakeupGainDb { get; set; } = 6.0;

    /// <summary>Song level under talk.</summary>
    public double DuckLevelDb { get; set; } = -12.0;

    /// <summary>Duck attack/release ramp.</summary>
    public int DuckRampMs { get; set; } = 800;

    /// <summary>EnergyFade overlap.</summary>
    public double DefaultCrossfadeSeconds { get; set; } = 5.0;

    /// <summary>Max ΔBPM for BeatAlignedFade.</summary>
    public double BeatAlignBpmTolerancePct { get; set; } = 5.0;

    public int HardCutGapAfterTalkMsMin { get; set; } = 200;

    public int HardCutGapAfterTalkMsMax { get; set; } = 600;

    public int HardCutGapSongMsMin { get; set; }

    public int HardCutGapSongMsMax { get; set; } = 150;

    /// <summary>Talk must end this long before the incoming song's IntroEnd.</summary>
    public int PostHitSafetyMs { get; set; } = 800;

    /// <summary>Per pair-kind strategy weight table; empty = built-in defaults.</summary>
    public string StrategyWeightsJson { get; set; } = string.Empty;

    /// <summary>If true, the track selector skips unanalysed items.</summary>
    public bool AnalysisRequired { get; set; }
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
    public static readonly IReadOnlyList<string> All = ["en", "de"];

    public static string Normalize(string? language)
        => All.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "en";
}

public static class TtsEngines
{
    public const string Kokoro = "kokoro";
    public const string Piper = "piper";
    public const string Qwen = "qwen";
    public const string ElevenLabs = "elevenlabs";
}
