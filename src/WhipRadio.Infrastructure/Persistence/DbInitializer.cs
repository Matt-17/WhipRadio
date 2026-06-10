using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Applies migrations and seeds moderators and station settings.
/// The weekly program plan is produced at runtime by the program director.</summary>
public static class DbInitializer
{
    public static async Task EnsureSeededAsync(RadioDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        if (!await db.Moderators.AnyAsync(ct))
        {
            db.Moderators.AddRange(SeedModerators());
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await PatchSeededModeratorsAsync(db, ct);
        }

        if (!await db.StationSettings.AnyAsync(ct))
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                StationName = "WhipRadio",
                DefaultLanguage = "en",
                TargetQueueLength = 3,
                AnnouncementEveryNTracks = 1,
            });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await PatchSettingsAsync(db, ct);
        }
    }

    /// <summary>
    /// Columns added by later migrations land as 0/false/"" on existing rows —
    /// which would leave the station off air with a zero-sized library. Restore
    /// the intended defaults when those impossible values are detected.
    /// </summary>
    private static async Task PatchSettingsAsync(RadioDbContext db, CancellationToken ct)
    {
        var settings = await db.StationSettings.FirstAsync(ct);
        if (settings.MaxLibrarySize > 0)
        {
            return; // already initialized
        }

        var defaults = new StationSettings();
        settings.MusicProductionEnabled = defaults.MusicProductionEnabled;
        settings.PlayoutEnabled = defaults.PlayoutEnabled;
        settings.MaxLibrarySize = defaults.MaxLibrarySize;
        settings.MinTrackDurationSeconds = defaults.MinTrackDurationSeconds;
        settings.MaxTrackDurationSeconds = defaults.MaxTrackDurationSeconds;
        settings.FrequencyMhz = defaults.FrequencyMhz;
        settings.FirstDayOfWeek = defaults.FirstDayOfWeek;
        settings.TextProvider = defaults.TextProvider;
        settings.OpenAiModel = defaults.OpenAiModel;
        settings.GreetingsEnabled = defaults.GreetingsEnabled;
        settings.MaxPendingGreetings = defaults.MaxPendingGreetings;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Databases created before gender/engine existed have every host on the
    /// default female Kokoro voice. Re-sync the well-known seeded hosts once.
    /// </summary>
    private static async Task PatchSeededModeratorsAsync(RadioDbContext db, CancellationToken ct)
    {
        var seeds = SeedModerators().ToDictionary(m => m.Name);
        var patched = false;

        foreach (var moderator in await db.Moderators.ToListAsync(ct))
        {
            // Pre-Phase-2 rows have empty Gender/TtsEngine (migration column default).
            var isStale = string.IsNullOrEmpty(moderator.TtsEngine) || string.IsNullOrEmpty(moderator.Gender);

            if (seeds.TryGetValue(moderator.Name, out var seed) && isStale)
            {
                moderator.Gender = seed.Gender;
                moderator.TtsEngine = seed.TtsEngine;
                moderator.VoiceId = seed.VoiceId;
                patched = true;
            }
            else if (isStale)
            {
                // Unknown legacy host: keep the voice, default the new fields.
                moderator.Gender = string.IsNullOrEmpty(moderator.Gender) ? ModeratorGenders.Female : moderator.Gender;
                moderator.TtsEngine = string.IsNullOrEmpty(moderator.TtsEngine) ? TtsEngines.Kokoro : moderator.TtsEngine;
                patched = true;
            }
        }

        if (patched)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static Moderator[] SeedModerators() =>
    [
        new()
        {
            Name = "Lena Spark",
            Language = "en",
            Gender = ModeratorGenders.Female,
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "af_heart",
            SpeechRate = 1.15,
            Style = "fast-energetic",
            Talkativeness = 0.8,
            PersonaPrompt =
                "You are Lena Spark, a young, bubbly radio host. You talk fast, with infectious " +
                "enthusiasm and boundless energy. You live for driving beats and celebrate every " +
                "new discovery like it's the hit of the year.",
            PrefersVocals = true,
            PreferredGenres = "indie rock,electronic",
            IsActive = true,
        },
        new()
        {
            Name = "Herbert Nightwave",
            Language = "en",
            Gender = ModeratorGenders.Male,
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "am_michael",
            SpeechRate = 0.85,
            Style = "slow-thoughtful",
            Talkativeness = 0.5,
            PersonaPrompt =
                "You are Herbert Nightwave, a measured, older radio host with a warm voice. " +
                "You speak slowly, love a well-placed pause, and sometimes drift into nostalgic " +
                "thoughts about the golden age of radio.",
            PrefersVocals = false,
            PreferredGenres = "lofi,jazz",
            IsActive = true,
        },
        new()
        {
            Name = "Charlie Wave",
            Language = "en",
            Gender = ModeratorGenders.Male,
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "bm_george",
            SpeechRate = 1.0,
            Style = "laid-back",
            Talkativeness = 0.35,
            PersonaPrompt =
                "You are Charlie Wave, a laid-back international host with a dry sense of humor. " +
                "You keep things smooth and casual, drop the occasional pun, and sound like you " +
                "are broadcasting from a beach bar at sunset.",
            PrefersVocals = null,
            PreferredGenres = "electronic,indie rock",
            IsActive = true,
        },
    ];
}
