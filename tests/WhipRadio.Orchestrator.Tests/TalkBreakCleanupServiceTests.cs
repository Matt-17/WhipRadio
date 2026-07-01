using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class TalkBreakCleanupServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task RunCleanupAsync_DeletesPlayedTalksOlderThanTwentyFourHours()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        Guid announcementId = Guid.NewGuid();
        await library.WriteAsync($"library/announcements/{announcementId}.wav", [1, 2, 3]);

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            SeedModerator(db);
            db.Announcements.Add(Announcement(announcementId, createdAt: Now.AddHours(-26)));
            db.TalkBreaks.Add(TalkBreak(announcementId, TalkBreakStatus.Played, Now.AddHours(-26), playedAtUtc: Now.AddHours(-25)));
            await db.SaveChangesAsync();
        }

        TalkBreakCleanupService service = CreateService(fixture, library.Root);

        await service.RunCleanupAsync(CancellationToken.None);

        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.TalkBreaks.CountAsync());
        Assert.Equal(0, await verify.TalkParts.CountAsync());
        Assert.Equal(0, await verify.Announcements.CountAsync());
        Assert.False(File.Exists(library.PathFor($"library/announcements/{announcementId}.wav")));
    }

    [TestMethod]
    public async Task RunCleanupAsync_KeepsPlayedTalksNewerThanTwentyFourHours()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        Guid announcementId = Guid.NewGuid();
        await library.WriteAsync($"library/announcements/{announcementId}.wav", [1, 2, 3]);

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            SeedModerator(db);
            db.Announcements.Add(Announcement(announcementId, createdAt: Now.AddHours(-23)));
            db.TalkBreaks.Add(TalkBreak(announcementId, TalkBreakStatus.Played, Now.AddHours(-23), playedAtUtc: Now.AddHours(-23)));
            await db.SaveChangesAsync();
        }

        TalkBreakCleanupService service = CreateService(fixture, library.Root);

        await service.RunCleanupAsync(CancellationToken.None);

        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.TalkBreaks.CountAsync());
        Assert.Equal(1, await verify.TalkParts.CountAsync());
        Assert.Equal(1, await verify.Announcements.CountAsync());
        Assert.True(File.Exists(library.PathFor($"library/announcements/{announcementId}.wav")));
    }

    [TestMethod]
    public async Task RunCleanupAsync_DoesNotDeleteTalkBitRenditionFileStillReferencedByBit()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        Guid announcementId = Guid.NewGuid();
        Guid talkBitId = Guid.NewGuid();
        string relativePath = $"library/announcements/{announcementId}.wav";
        await library.WriteAsync(relativePath, [1, 2, 3]);

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            SeedModerator(db);
            db.TalkBits.Add(new TalkBit
            {
                Id = talkBitId,
                ModeratorId = 1,
                Premise = "evergreen bit",
                CreatedAtUtc = Now.AddDays(-3),
                Renditions =
                [
                    new TalkBitRendition
                    {
                        Id = Guid.NewGuid(),
                        Text = "saved rendition",
                        FilePath = relativePath,
                        DurationSeconds = 3,
                        CreatedAtUtc = Now.AddDays(-3),
                    },
                ],
            });
            db.Announcements.Add(Announcement(announcementId, createdAt: Now.AddHours(-26), relativePath));
            db.TalkBreaks.Add(TalkBreak(announcementId, TalkBreakStatus.Played, Now.AddHours(-26), playedAtUtc: Now.AddHours(-25)));
            await db.SaveChangesAsync();
        }

        TalkBreakCleanupService service = CreateService(fixture, library.Root);

        await service.RunCleanupAsync(CancellationToken.None);

        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.TalkBreaks.CountAsync());
        Assert.Equal(0, await verify.Announcements.CountAsync());
        Assert.Equal(1, await verify.TalkBitRenditions.CountAsync());
        Assert.True(File.Exists(library.PathFor(relativePath)));
    }

    private static TalkBreakCleanupService CreateService(DbFixture fixture, string root)
        => new(
            fixture,
            Options.Create(new RadioOptions { DataRoot = root }),
            new FixedTimeProvider(Now),
            NullLogger<TalkBreakCleanupService>.Instance);

    private static void SeedModerator(RadioDbContext db)
    {
        db.Moderators.Add(new Moderator
        {
            Id = 1,
            Name = "Test Host",
            Slug = "test-host",
            PersonaPrompt = "Test host.",
            Style = "steady",
        });
    }

    private static Announcement Announcement(Guid id, DateTime createdAt, string? relativePath = null)
        => new()
        {
            Id = id,
            ModeratorId = 1,
            Kind = AnnouncementKind.Banter,
            ScriptText = "hello",
            VoicedText = "hello",
            FilePath = relativePath ?? $"library/announcements/{id}.wav",
            DurationSeconds = 3,
            CreatedAt = createdAt,
            WasPlayed = true,
        };

    private static TalkBreak TalkBreak(
        Guid announcementId,
        TalkBreakStatus status,
        DateTime createdAtUtc,
        DateTime? playedAtUtc = null,
        DateTime? expiresAtUtc = null)
        => new()
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcementId,
            ModeratorId = 1,
            Status = status,
            Purpose = "Banter",
            CreatedAtUtc = createdAtUtc,
            RenderedAtUtc = createdAtUtc,
            PlayedAtUtc = playedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            DurationSeconds = 3,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = TalkPartKind.Banter,
                    Status = status == TalkBreakStatus.Played ? TalkPartStatus.Played : TalkPartStatus.Rendered,
                    Purpose = "Banter",
                    AnnouncementId = announcementId,
                    CreatedAtUtc = createdAtUtc,
                    ExpiresAtUtc = expiresAtUtc,
                },
            ],
        };

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class TempLibrary : IDisposable
    {
        public TempLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "whipradio-talk-cleanup-tests", Guid.NewGuid().ToString("N"));
        }

        public string Root { get; }

        public string PathFor(string relativePath) => Path.Combine(Root, relativePath);

        public async Task WriteAsync(string relativePath, byte[] bytes)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
