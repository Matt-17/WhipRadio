using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.TestSupport;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class DbInitializerTests
{
    [TestMethod]
    public async Task EnsureSeededAsync_UpdatesLegacyDefaultTrackDurationRange()
    {
        await using var fixture = await DbFixture.CreateAsync();

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                MinTrackDurationSeconds = 150,
                MaxTrackDurationSeconds = 480,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await DbInitializer.EnsureSeededAsync(db);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.StationSettings.SingleAsync();
            Assert.Equal(150, settings.MinTrackDurationSeconds);
            Assert.Equal(300, settings.MaxTrackDurationSeconds);
        }
    }

    [TestMethod]
    public async Task EnsureSeededAsync_MarksAbandonedRunningStudioHistoryFailed()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var runningId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.StudioHistory.Add(new StudioHistoryEntry
            {
                Id = runningId,
                StudioName = "Studio #1",
                StudioKind = StudioKind.Recording,
                Provider = "ace-step-1.5",
                Operation = "Recording for test",
                Status = StudioHistoryStatus.Running,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                Prompt = "Prompt",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await DbInitializer.EnsureSeededAsync(db);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var entry = await db.StudioHistory.SingleAsync(h => h.Id == runningId);
            Assert.Equal(StudioHistoryStatus.Failed, entry.Status);
            Assert.NotNull(entry.CompletedAtUtc);
            Assert.Contains("Orchestrator stopped", entry.Error);
        }
    }

    [TestMethod]
    public async Task EnsureSeededAsync_MarksExistingPackageAnnouncementsScheduledOnly()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var announcementId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Moderators.Add(new Moderator
            {
                Id = 1,
                Name = "Maya",
                Language = "en",
                Gender = ModeratorGenders.Female,
                TtsEngine = TtsEngines.Kokoro,
                VoiceId = "af_bella",
            });
            db.Announcements.Add(new Announcement
            {
                Id = announcementId,
                ModeratorId = 1,
                Kind = AnnouncementKind.News,
                ScriptText = "script",
                VoicedText = "voice",
                FilePath = "library/announcements/package.wav",
                DurationSeconds = 60,
                CreatedAt = DateTime.UtcNow,
                PlayoutIntent = AnnouncementPlayoutIntent.Immediate,
            });
            db.NewsPackages.Add(new NewsPackage
            {
                Id = Guid.NewGuid(),
                Kind = NewsPackageKind.TopOfHour,
                Status = NewsPackageStatus.Ready,
                TargetUtc = DateTime.UtcNow.AddMinutes(5),
                TargetDurationSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow,
                AnnouncementId = announcementId,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await DbInitializer.EnsureSeededAsync(db);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var announcement = await db.Announcements.SingleAsync(a => a.Id == announcementId);
            Assert.Equal(AnnouncementPlayoutIntent.ScheduledOnly, announcement.PlayoutIntent);
        }
    }
}
