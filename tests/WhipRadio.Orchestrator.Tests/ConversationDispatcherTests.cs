using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ConversationDispatcherTests
{
    /// <summary>Whole-second UTC now — Postgres timestamptz keeps microseconds only,
    /// so sub-µs ticks would break the (ItemId, TargetUtc) identity checks.</summary>
    private static DateTime UtcNowSecond()
        => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    [TestMethod]
    public async Task DueEpisode_MixerEnabled_SchedulesTimedInterruptAndMarksQueued()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var target = UtcNowSecond();
        var (segmentId, announcementId) = await SeedProducedEpisodeAsync(fixture, target, mixerEnabled: true);

        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var queue = new FakePlayoutQueue();
        var dispatcher = CreateDispatcher(fixture, interrupts, queue);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        Assert.True(interrupts.HasPending(announcementId, target));
        Assert.Equal(0, queue.FrontItems.Count);

        await using var db = fixture.CreateDbContext();
        var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
        Assert.Equal(ConversationStatus.Queued, segment.Status);
    }

    [TestMethod]
    public async Task DueEpisode_LegacyPath_EnqueuesAtQueueFront()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var target = UtcNowSecond();
        var (_, announcementId) = await SeedProducedEpisodeAsync(fixture, target, mixerEnabled: false);

        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var queue = new FakePlayoutQueue();
        var dispatcher = CreateDispatcher(fixture, interrupts, queue);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        Assert.Equal(1, queue.FrontItems.Count);
        Assert.Equal(announcementId, queue.FrontItems[0].ItemId);
        Assert.False(interrupts.HasPending(announcementId, target));
    }

    [TestMethod]
    public async Task PlayedEpisode_IsMarkedUsedAndNeverReArmed()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var target = UtcNowSecond();
        var (segmentId, announcementId) = await SeedProducedEpisodeAsync(fixture, target, mixerEnabled: true);

        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var dispatcher = CreateDispatcher(fixture, interrupts, new FakePlayoutQueue());
        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        // The mixer consumed the interrupt and the reporter flipped WasPlayed.
        _ = interrupts.TryConsume(target);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Announcements
                .Where(a => a.Id == announcementId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WasPlayed, true));
        }

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);
        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Used, segment.Status);
            Assert.NotNull(segment.UsedAtUtc);
        }

        Assert.False(interrupts.HasPending(announcementId, target), "a played episode must not be re-armed");
    }

    [TestMethod]
    public async Task OverdueEpisode_FailsCleanly()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var target = UtcNowSecond().AddMinutes(-20); // far past the 5-min late window
        var (segmentId, _) = await SeedProducedEpisodeAsync(fixture, target, mixerEnabled: true);

        var dispatcher = CreateDispatcher(
            fixture,
            new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance),
            new FakePlayoutQueue());
        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
        Assert.Equal(ConversationStatus.Failed, segment.Status);
        Assert.Contains("slot", segment.FailureReason!);
    }

    private static ConversationDispatcher CreateDispatcher(
        DbFixture fixture, TimedPlayoutInterruptService interrupts, FakePlayoutQueue queue)
        => new(
            fixture,
            queue,
            interrupts,
            TimeProvider.System,
            new NoOpPublisher(),
            NullLogger<ConversationDispatcher>.Instance);

    private static async Task<(Guid SegmentId, Guid AnnouncementId)> SeedProducedEpisodeAsync(
        DbFixture fixture, DateTime targetUtc, bool mixerEnabled)
    {
        await using var db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                MixerEnabled = mixerEnabled,
            });
        }

        var host = new Moderator
        {
            Name = "Nova Quinn",
            Slug = "nova-quinn",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "host",
            Style = "calm",
            IsActive = true,
        };
        db.Moderators.Add(host);
        await db.SaveChangesAsync();

        var announcement = new Announcement
        {
            Id = Guid.NewGuid(),
            ModeratorId = host.Id,
            Kind = AnnouncementKind.Conversation,
            FilePath = "library/conversations/episode.wav",
            DurationSeconds = 900,
            CreatedAt = DateTime.UtcNow,
            PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly,
        };
        db.Announcements.Add(announcement);

        var segment = new ConversationSegment
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Podcast,
            Topic = "Weekly show",
            Title = "Night Static",
            Status = ConversationStatus.Produced,
            TargetUtc = targetUtc,
            PodcastShowId = null,
            AnnouncementId = announcement.Id,
            OutputFilePath = announcement.FilePath,
            DurationSeconds = 900,
            CreatedAtUtc = DateTime.UtcNow,
            ProducedAtUtc = DateTime.UtcNow,
        };
        db.ConversationSegments.Add(segment);
        await db.SaveChangesAsync();
        return (segment.Id, announcement.Id);
    }

    private sealed class FakePlayoutQueue : IPlayoutQueue
    {
        public List<PlayoutItem> FrontItems { get; } = [];

        public List<PlayoutItem> Items { get; } = [];

        public int Count => Items.Count + FrontItems.Count;

        public void Enqueue(PlayoutItem item) => Items.Add(item);

        public void EnqueueFront(PlayoutItem item) => FrontItems.Add(item);

        public PlayoutItem? PeekNext() => FrontItems.FirstOrDefault() ?? Items.FirstOrDefault();

        public Task<PlayoutItem> DequeueAsync(CancellationToken ct)
            => Task.FromException<PlayoutItem>(new InvalidOperationException("Not used in these tests."));
    }

    private sealed class NoOpPublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishConversationsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
