using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class AnnouncementPlayoutIntentTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void ImmediatePlayableAnnouncements_ExcludesScheduledOnlyPackageWeather()
    {
        var immediateId = Guid.NewGuid();
        var scheduledOnlyId = Guid.NewGuid();
        var announcements = new[]
        {
            Announcement(immediateId, AnnouncementKind.Weather, AnnouncementPlayoutIntent.Immediate),
            Announcement(scheduledOnlyId, AnnouncementKind.Weather, AnnouncementPlayoutIntent.ScheduledOnly),
        }.AsQueryable();

        var playable = ShowRunnerService
            .ImmediatePlayableAnnouncements(announcements, [immediateId, scheduledOnlyId], Now.AddMinutes(-30))
            .Select(announcement => announcement.Id)
            .ToList();

        Assert.Equal(new[] { immediateId }, playable);
    }

    [TestMethod]
    public async Task PriorityDispatcher_DoesNotFrontPushScheduledOnlyAnnouncements()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var immediateId = Guid.NewGuid();
        var scheduledOnlyId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Moderators.Add(new Moderator
            {
                Id = 1,
                Name = "Maya",
                Language = "en",
                Gender = ModeratorGenders.Female,
                TtsEngine = TtsEngines.Kokoro,
                VoiceId = "af_bella",
            });
            db.Announcements.Add(Announcement(immediateId, AnnouncementKind.StationId, AnnouncementPlayoutIntent.Immediate));
            db.Announcements.Add(Announcement(scheduledOnlyId, AnnouncementKind.News, AnnouncementPlayoutIntent.ScheduledOnly));
            db.TalkBreaks.Add(TalkBreak(immediateId, "ImmediateHigh"));
            db.TalkBreaks.Add(TalkBreak(scheduledOnlyId, "TopOfHourPackage"));
            await db.SaveChangesAsync();
        }

        var queue = new FakePlayoutQueue();
        var dispatcher = new PriorityTalkBreakDispatcher(
            fixture,
            queue,
            new QueueStateTracker(),
            new FixedTimeProvider(Now),
            NullLogger<PriorityTalkBreakDispatcher>.Instance);

        var pushed = await dispatcher.PushReadyAsync(CancellationToken.None);

        Assert.Equal(1, pushed);
        Assert.Equal(new[] { immediateId }, queue.Enqueued.Select(item => item.ItemId).ToList());
    }

    private static Announcement Announcement(Guid id, AnnouncementKind kind, AnnouncementPlayoutIntent intent) => new()
    {
        Id = id,
        ModeratorId = 1,
        Kind = kind,
        ScriptText = "script",
        VoicedText = "voice",
        FilePath = $"library/announcements/{id}.wav",
        DurationSeconds = 10,
        CreatedAt = Now.AddMinutes(-5),
        WasPlayed = false,
        PlayoutIntent = intent,
    };

    private static TalkBreak TalkBreak(Guid announcementId, string purpose) => new()
    {
        Id = Guid.NewGuid(),
        AnnouncementId = announcementId,
        ModeratorId = 1,
        Priority = TalkBreakPriority.High,
        Status = TalkBreakStatus.Rendered,
        Purpose = purpose,
        Title = purpose,
        CreatedAtUtc = Now.AddMinutes(-5),
        RenderedAtUtc = Now.AddMinutes(-5),
        ExpiresAtUtc = Now.AddMinutes(10),
        DurationSeconds = 10,
        Parts =
        [
            new TalkPart
            {
                SortOrder = 0,
                Kind = TalkPartKind.News,
                Status = TalkPartStatus.Rendered,
                Priority = TalkBreakPriority.High,
                Purpose = purpose,
                AnnouncementId = announcementId,
                CreatedAtUtc = Now.AddMinutes(-5),
                ExpiresAtUtc = Now.AddMinutes(10),
            },
        ],
    };

    private sealed class FakePlayoutQueue : IPlayoutQueue
    {
        public List<PlayoutItem> Enqueued { get; } = [];

        public int Count => Enqueued.Count;

        public void Enqueue(PlayoutItem item) => Enqueued.Add(item);

        public void EnqueueFront(PlayoutItem item) => Enqueued.Insert(0, item);

        public PlayoutItem? PeekNext() => Enqueued.FirstOrDefault();

        public Task<PlayoutItem> DequeueAsync(CancellationToken ct)
        {
            var item = Enqueued[0];
            Enqueued.RemoveAt(0);
            return Task.FromResult(item);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
