using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Applies migrations and seeds moderators, schedule and station settings.</summary>
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

        if (!await db.ScheduleSlots.AnyAsync(ct))
        {
            var moderators = await db.Moderators.OrderBy(m => m.Id).ToListAsync(ct);
            db.ScheduleSlots.AddRange(SeedSchedule(moderators));
            await db.SaveChangesAsync(ct);
        }
    }

    private static Moderator[] SeedModerators() =>
    [
        new()
        {
            Name = "Lena Funkturm",
            Language = "de",
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
            VoiceId = "af_heart",
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
            VoiceId = "af_heart",
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

    private static IEnumerable<ScheduleSlot> SeedSchedule(IReadOnlyList<Moderator> moderators)
    {
        // Rotate 3 genres across the day; moderators take 8-hour shifts.
        string[] genres = ["lofi", "indie rock", "electronic"];
        for (var hour = 0; hour < 24; hour++)
        {
            yield return new ScheduleSlot
            {
                HourOfDay = hour,
                Genre = genres[hour % genres.Length],
                ModeratorId = moderators[hour / 8 % moderators.Count].Id,
            };
        }
    }
}
