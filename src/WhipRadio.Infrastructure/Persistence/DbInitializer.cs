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
    }

    private static Moderator[] SeedModerators() =>
    [
        new()
        {
            Name = "Lena Funkturm",
            Language = "de",
            Gender = ModeratorGenders.Female,
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "af_heart",
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
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "am_michael",
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
