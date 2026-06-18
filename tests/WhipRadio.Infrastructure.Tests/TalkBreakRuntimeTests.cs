using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class TalkBreakRuntimeTests
{
    private static readonly DateTime Now = new(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task PriorityDispatcher_FrontPushesEmergencyBeforeHigh()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Moderators.Add(Host());
            AddTalkBreak(db, TalkBreakPriority.High, Now.AddMinutes(-20), out var highId);
            AddTalkBreak(db, TalkBreakPriority.Emergency, Now.AddMinutes(-2), out var newEmergencyId);
            AddTalkBreak(db, TalkBreakPriority.Emergency, Now.AddMinutes(-10), out var oldEmergencyId);
            await db.SaveChangesAsync();

            var root = Path.Combine(Path.GetTempPath(), "whipradio-queue-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var tracker = new QueueStateTracker();
                var stateStore = new PlayoutStateStore(
                    Options.Create(new RadioOptions { DataRoot = root }),
                    new FixedTimeProvider(Now),
                    NullLogger<PlayoutStateStore>.Instance);
                IPlayoutQueue queue = new TrackedPlayoutQueue(new ChannelPlayoutQueue(), tracker, stateStore);
                var dispatcher = new PriorityTalkBreakDispatcher(
                    fixture,
                    queue,
                    tracker,
                    new FixedTimeProvider(Now),
                    NullLogger<PriorityTalkBreakDispatcher>.Instance);

                var pushed = await dispatcher.PushReadyAsync(CancellationToken.None);

                Assert.Equal(3, pushed);
                Assert.Equal(oldEmergencyId, (await queue.DequeueAsync(CancellationToken.None)).ItemId);
                Assert.Equal(newEmergencyId, (await queue.DequeueAsync(CancellationToken.None)).ItemId);
                Assert.Equal(highId, (await queue.DequeueAsync(CancellationToken.None)).ItemId);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    [TestMethod]
    public async Task Cleanup_ExpiresTalkBreakAndDeletesUnplayedAnnouncement()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "whipradio-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var announcementId = Guid.NewGuid();
            var relativePath = Path.Combine("library", "announcements", $"{announcementId}.wav");
            var absolutePath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

            await using (var db = fixture.CreateDbContext())
            {
                db.Moderators.Add(Host());
                db.Announcements.Add(Announcement(announcementId, relativePath));
                db.TalkBreaks.Add(new TalkBreak
                {
                    Id = Guid.NewGuid(),
                    AnnouncementId = announcementId,
                    ModeratorId = 1,
                    Priority = TalkBreakPriority.Scheduled,
                    Status = TalkBreakStatus.Rendered,
                    Purpose = "Weather",
                    Title = "Announcement",
                    CreatedAtUtc = Now.AddHours(-1),
                    RenderedAtUtc = Now.AddHours(-1),
                    ExpiresAtUtc = Now.AddMinutes(-1),
                    Parts =
                    [
                        new TalkPart
                        {
                            SortOrder = 0,
                            Kind = TalkPartKind.Weather,
                            Status = TalkPartStatus.Rendered,
                            Priority = TalkBreakPriority.Scheduled,
                            Purpose = "Weather",
                            AnnouncementId = announcementId,
                            CreatedAtUtc = Now.AddHours(-1),
                            ExpiresAtUtc = Now.AddMinutes(-1),
                        },
                    ],
                });
                await db.SaveChangesAsync();
            }

            var cleanup = new TalkBreakCleanupService(
                fixture,
                Options.Create(new RadioOptions { DataRoot = root }),
                new FixedTimeProvider(Now),
                NullLogger<TalkBreakCleanupService>.Instance);

            await cleanup.RunCleanupAsync(CancellationToken.None);

            await using var verify = fixture.CreateDbContext();
            Assert.False(await verify.Announcements.AnyAsync(item => item.Id == announcementId));
            var talkBreak = await verify.TalkBreaks.Include(item => item.Parts).SingleAsync();
            Assert.Equal(TalkBreakStatus.Expired, talkBreak.Status);
            Assert.Equal(TalkPartStatus.Expired, talkBreak.Parts.Single().Status);
            Assert.False(File.Exists(absolutePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task TalkParts_LoadInSortOrderForPlayLogExpansion()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var announcementId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Moderators.Add(Host());
            db.Announcements.Add(Announcement(announcementId, "library/announcements/test.wav"));
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = Now,
                ItemType = PlayoutItemType.Announcement,
                ItemId = announcementId,
                ModeratorId = 1,
                DurationSeconds = 12,
            });
            db.TalkBreaks.Add(new TalkBreak
            {
                Id = Guid.NewGuid(),
                AnnouncementId = announcementId,
                ModeratorId = 1,
                Priority = TalkBreakPriority.Normal,
                Status = TalkBreakStatus.Played,
                Purpose = "Composite",
                Title = "Announcement",
                CreatedAtUtc = Now,
                Parts =
                [
                    Part(1, TalkPartKind.NextSongIntro, "Intro next"),
                    Part(0, TalkPartKind.PreviousSongComment, "Back announce"),
                ],
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var announcementIds = await db.PlayLog.AsNoTracking()
                .Where(entry => entry.ItemType == PlayoutItemType.Announcement)
                .Select(entry => entry.ItemId)
                .ToListAsync();
            var talkBreaks = await db.TalkBreaks.AsNoTracking()
                .Include(talkBreak => talkBreak.Parts)
                .Where(talkBreak => talkBreak.AnnouncementId != null
                    && announcementIds.Contains(talkBreak.AnnouncementId.Value))
                .ToDictionaryAsync(talkBreak => talkBreak.AnnouncementId!.Value);

            var purposes = talkBreaks[announcementId].Parts
                .OrderBy(part => part.SortOrder)
                .Select(part => part.Purpose)
                .ToList();

            CollectionAssert.AreEqual(new[] { "Back announce", "Intro next" }, purposes);
        }
    }

    [TestMethod]
    public async Task SegmentRenderer_RendersOrderedPartsIntoSingleAnnouncement()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "whipradio-segment-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "library", "announcements"));
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var firstPath = Path.Combine("library", "announcements", $"{firstId}.wav");
            var secondPath = Path.Combine("library", "announcements", $"{secondId}.wav");
            await File.WriteAllBytesAsync(
                Path.Combine(root, firstPath),
                WavFile.WrapPcm16(new byte[88200], 44100, 1));
            await File.WriteAllBytesAsync(
                Path.Combine(root, secondPath),
                WavFile.WrapPcm16(new byte[44100], 44100, 1));

            await using (var db = fixture.CreateDbContext())
            {
                db.Moderators.Add(Host());
                db.Announcements.Add(SourceAnnouncement(firstId, firstPath, AnnouncementKind.SongOutro, "Back announce", 1));
                db.Announcements.Add(SourceAnnouncement(secondId, secondPath, AnnouncementKind.SongIntro, "Next intro", 0.5));
                db.TalkBreaks.Add(SourceBreak(firstId, TalkPartKind.PreviousSongComment, "Back announce"));
                db.TalkBreaks.Add(SourceBreak(secondId, TalkPartKind.NextSongIntro, "Intro next"));
                await db.SaveChangesAsync();
            }

            var recorder = new MediaAnalysisRecorder(
                new ThrowingAnalysisClient(),
                fixture,
                NullLogger<MediaAnalysisRecorder>.Instance);
            var renderer = new SegmentRenderer(
                fixture,
                Options.Create(new RadioOptions { DataRoot = root }),
                recorder,
                new FixedTimeProvider(Now),
                NullLogger<SegmentRenderer>.Instance);

            var composite = await renderer.RenderAsync(
                [
                    SourceAnnouncement(firstId, firstPath, AnnouncementKind.SongOutro, "Back announce", 1),
                    SourceAnnouncement(secondId, secondPath, AnnouncementKind.SongIntro, "Next intro", 0.5),
                ],
                Host(),
                CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(root, composite.FilePath)));
            Assert.Equal(1.5, composite.DurationSeconds, precision: 6);

            await using var verify = fixture.CreateDbContext();
            var compositeBreak = await verify.TalkBreaks
                .Include(talkBreak => talkBreak.Parts)
                .SingleAsync(talkBreak => talkBreak.AnnouncementId == composite.Id);
            var orderedPurposes = compositeBreak.Parts
                .OrderBy(part => part.SortOrder)
                .Select(part => part.Purpose)
                .ToList();

            CollectionAssert.AreEqual(new[] { "Back announce", "Intro next" }, orderedPurposes);
            Assert.True(compositeBreak.Parts.All(part => part.AnnouncementId == composite.Id));
            Assert.Equal(2, await verify.TalkBreaks.CountAsync(talkBreak =>
                (talkBreak.AnnouncementId == firstId || talkBreak.AnnouncementId == secondId)
                && talkBreak.Status == TalkBreakStatus.Expired));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Moderator Host()
        => new()
        {
            Id = 1,
            Name = "Ava",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "warm",
            Style = "calm",
        };

    private static Announcement Announcement(Guid id, string filePath)
        => new()
        {
            Id = id,
            ModeratorId = 1,
            Kind = AnnouncementKind.Banter,
            ScriptText = "Hello",
            VoicedText = "Hello",
            FilePath = filePath,
            DurationSeconds = 12,
            CreatedAt = Now,
        };

    private static Announcement SourceAnnouncement(
        Guid id,
        string filePath,
        AnnouncementKind kind,
        string text,
        double durationSeconds)
        => new()
        {
            Id = id,
            ModeratorId = 1,
            Kind = kind,
            ScriptText = text,
            VoicedText = text,
            FilePath = filePath,
            DurationSeconds = durationSeconds,
            CreatedAt = Now,
        };

    private static TalkPart Part(int sortOrder, TalkPartKind kind, string purpose)
        => new()
        {
            SortOrder = sortOrder,
            Kind = kind,
            Status = TalkPartStatus.Rendered,
            Priority = TalkBreakPriority.Normal,
            Purpose = purpose,
            CreatedAtUtc = Now,
        };

    private static TalkBreak SourceBreak(Guid announcementId, TalkPartKind kind, string purpose)
        => new()
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcementId,
            ModeratorId = 1,
            Priority = TalkBreakPriority.Normal,
            Status = TalkBreakStatus.Rendered,
            Purpose = purpose,
            Title = "Announcement",
            CreatedAtUtc = Now,
            RenderedAtUtc = Now,
            DurationSeconds = 1,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = kind,
                    Status = TalkPartStatus.Rendered,
                    Priority = TalkBreakPriority.Normal,
                    Purpose = purpose,
                    AnnouncementId = announcementId,
                    CreatedAtUtc = Now,
                },
            ],
        };

    private static void AddTalkBreak(
        RadioDbContext db,
        TalkBreakPriority priority,
        DateTime createdAt,
        out Guid announcementId)
    {
        announcementId = Guid.NewGuid();
        db.Announcements.Add(Announcement(announcementId, $"library/announcements/{announcementId}.wav"));
        db.TalkBreaks.Add(new TalkBreak
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcementId,
            ModeratorId = 1,
            Priority = priority,
            Status = TalkBreakStatus.Rendered,
            Purpose = priority.ToString(),
            Title = "Announcement",
            CreatedAtUtc = createdAt,
            ExpiresAtUtc = Now.AddMinutes(30),
            DurationSeconds = 10,
        });
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class ThrowingAnalysisClient : IAudioAnalysisClient
    {
        public Task<MediaAnalysisDto> AnalyzeAsync(string relativePath, AnalysisMode mode, CancellationToken ct)
            => throw new InvalidOperationException("analysis unavailable in test");

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (var db = new RadioDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new DbFixture(connection, options);
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
