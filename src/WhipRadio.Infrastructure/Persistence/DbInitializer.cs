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
                DefaultLanguage = "de",
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
            Name = "Lena Funkturm",
            Language = "de",
            Gender = ModeratorGenders.Female,
            TtsEngine = TtsEngines.Piper,
            VoiceId = "de_DE-eva_k-x_low",
            SpeechRate = 1.15,
            Style = "fast-energetic",
            PersonaPrompt =
                "Du bist Lena Funkturm, eine junge, quirlige Moderatorin. Du sprichst schnell, " +
                "begeistert und mit viel Energie. Du liebst treibende Beats und feierst jede " +
                "Neuentdeckung, als wäre sie der Hit des Jahres.",
            PrefersVocals = true,
            PreferredGenres = "indie rock,electronic",
            IsActive = true,
        },
        new()
        {
            Name = "Herbert Nachtwelle",
            Language = "de",
            Gender = ModeratorGenders.Male,
            TtsEngine = TtsEngines.Piper,
            VoiceId = "de_DE-thorsten-medium",
            SpeechRate = 0.85,
            Style = "slow-thoughtful",
            PersonaPrompt =
                "Du bist Herbert Nachtwelle, ein bedächtiger älterer Radiomoderator mit warmer " +
                "Stimme. Du sprichst langsam, machst gerne kleine Kunstpausen und verlierst dich " +
                "manchmal in nostalgischen Gedanken über die gute alte Radiozeit.",
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
