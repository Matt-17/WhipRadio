using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Entities;

/// <summary>Single-row station configuration (Id = 1).</summary>
public class StationSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string StationName { get; set; } = "WhipRadio";

    public string StationSlogan { get; set; } = "Llamas whipped the radio's mix.";

    public string StationVision { get; set; } =
        "A living AI radio station with original music, distinct hosts, and a coherent on-air identity.";

    public string StationMission { get; set; } =
        "Create a continuous local radio experience where music, talk, weather, and listener moments feel intentional.";

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

    /// <summary>Generated track length range.</summary>
    public int MinTrackDurationSeconds { get; set; } = 150;

    public int MaxTrackDurationSeconds { get; set; } = 300;

    // --- Speech ------------------------------------------------------------------

    /// <summary>[breath] markers sound bad on some engines — off by default.</summary>
    public bool EnableBreathMarkers { get; set; }

    // --- Station defaults ---------------------------------------------------------

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

    // --- Weather ---------------------------------------------------------------

    public bool WeatherEnabled { get; set; } = true;

    public int WeatherCadenceMinutes { get; set; } = 60;

    public int? WeatherSpecialistModeratorId { get; set; }

    public string WeatherLocationName { get; set; } = "New York, US";

    public double WeatherLatitude { get; set; } = 40.7128;

    public double WeatherLongitude { get; set; } = -74.0060;

    /// <summary>Reserved for a later full show handover; default flow is a quick cutaway.</summary>
    public bool WeatherFullHandoverEnabled { get; set; }

    // --- News / top-of-hour production -------------------------------------------

    public bool NewsEnabled { get; set; } = true;

    public bool NewsExtractionEnabled { get; set; } = true;

    public int NewsPackageCadenceMinutes { get; set; } = 60;

    public int NewsPackageMaxDurationSeconds { get; set; } = 300;

    public int? NewsPresenterModeratorId { get; set; }

    public bool NewsSeedFeedsCreated { get; set; }

    public string NewsCategoryOrder { get; set; } = "general,business,technology,sports,culture,regional";

    public double TopOfHourFadeOutSeconds { get; set; } = 1.0;

    public int TopOfHourIntroGraceSeconds { get; set; } = TopOfHourScheduler.DefaultIntroGraceSeconds;

    // --- Long news format (scheduled news show blocks) ----------------------------

    public bool NewsLongFormatEnabled { get; set; }

    /// <summary>CSV of local HH:mm air times; each seeds a daily news-show slot in the grid.</summary>
    public string NewsLongFormatAirTimes { get; set; } = "08:00,20:00";

    public int NewsLongFormatDurationMinutes { get; set; } = 30;

    /// <summary>The seeded news-show Format; lets the seeder update/remove its own slots only.</summary>
    public Guid? NewsShowFormatId { get; set; }

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

    // --- Track selection diversity --------------------------------------------

    /// <summary>Master switch for the format-aware diversity rules. Off = legacy last-N exclusion.</summary>
    public bool SelectionDiversityEnabled { get; set; } = true;

    /// <summary>Absolute floor: the last N played tracks are always excluded, never relaxed away.</summary>
    public int RecentExclusionCount { get; set; } = 5;

    /// <summary>Max plays by one artist within the lookback window (StandardRotation).</summary>
    public int DefaultMaxArtistPlaysPerHour { get; set; } = 2;

    /// <summary>How many recent plays the artist-repeat cap looks back at.</summary>
    public int DefaultArtistLookbackTracks { get; set; } = 8;

    /// <summary>Play-count fatigue coefficient in TrackWeighting. Higher = heavy-rotation tracks fade faster.</summary>
    public double FatigueFactor { get; set; } = 0.15;

    // --- Archive (imported real music, Phase 6a) --------------------------------

    /// <summary>Allow uploading music files on the Archive page.</summary>
    public bool ArchiveUploadEnabled { get; set; } = true;

    /// <summary>Let imported (uploaded/external) tracks enter normal rotation.</summary>
    public bool ArchivePlayoutEnabled { get; set; } = true;

    /// <summary>Background metadata enrichment (MusicBrainz/Wikidata/Wikipedia) for imported tracks.</summary>
    public bool ArchiveEnrichmentEnabled { get; set; } = true;

    /// <summary>Give podcast/chat hosts access to the knowledge base (digests + LookupKnowledge verb).</summary>
    public bool PodcastKnowledgeEnabled { get; set; } = true;

    // --- Chat control ---------------------------------------------------------

    public int ChatMaxAgentHops { get; set; } = 6;

    public int ChatHistoryPromptMessages { get; set; } = 20;

    public int ChatRetainedMessagesPerChannel { get; set; } = 500;
}

public static class TextProviders
{
    public const string Ollama = "ollama";
    public const string OpenAi = "openai";
}

/// <summary>
/// Languages the station can broadcast in (limited by local TTS voice support).
/// The station language is the main language: hosts and all spoken texts follow it.
/// The set is English plus the full Qwen3-TTS language list.
/// </summary>
public static class StationLanguages
{
    public static readonly IReadOnlyList<string> All =
        ["en", "de", "es", "fr", "it", "pt", "ru", "zh", "ja", "ko"];

    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["de"] = "German",
            ["es"] = "Spanish",
            ["fr"] = "French",
            ["it"] = "Italian",
            ["pt"] = "Portuguese",
            ["ru"] = "Russian",
            ["zh"] = "Chinese",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
        };

    public static string Normalize(string? language)
        => All.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "en";

    /// <summary>English display name for a language code (falls back to English).</summary>
    public static string DisplayName(string? language)
        => Names.TryGetValue(Normalize(language), out var name) ? name : "English";
}

public static class TtsEngines
{
    public const string Kokoro = "kokoro";
    public const string Piper = "piper";
    public const string Qwen = "qwen";
    public const string ElevenLabs = "elevenlabs";
}
