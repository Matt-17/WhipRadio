using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class PriorityTalkBreakDispatcherConcurrencyTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task PushReadyAsync_UnderConcurrentCallers_FrontPushesEachAnnouncementExactlyOnce()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var announcementIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

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
            foreach (var id in announcementIds)
            {
                db.Announcements.Add(Announcement(id));
                db.TalkBreaks.Add(TalkBreak(id));
            }

            await db.SaveChangesAsync();
        }

        var queue = new ConcurrentFakePlayoutQueue();
        var dispatcher = new PriorityTalkBreakDispatcher(
            fixture,
            queue,
            new QueueStateTracker(),
            new FixedTimeProvider(Now),
            NullLogger<PriorityTalkBreakDispatcher>.Instance);

        // The dispatcher is hit concurrently in production from the show loop, an
        // HTTP endpoint, and the chat worker; each announcement must still be
        // front-pushed exactly once.
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => dispatcher.PushReadyAsync(CancellationToken.None)))
            .ToArray();
        var pushedPerCaller = await Task.WhenAll(callers);

        Assert.Equal(announcementIds.Count, pushedPerCaller.Sum());
        var enqueuedIds = queue.SnapshotIds();
        Assert.Equal(announcementIds.Count, enqueuedIds.Count);
        Assert.Equal(announcementIds.OrderBy(id => id).ToList(), enqueuedIds.OrderBy(id => id).ToList());
    }

    private static Announcement Announcement(Guid id) => new()
    {
        Id = id,
        ModeratorId = 1,
        Kind = AnnouncementKind.StationId,
        ScriptText = "script",
        VoicedText = "voice",
        FilePath = $"library/announcements/{id}.wav",
        DurationSeconds = 10,
        CreatedAt = Now.AddMinutes(-5),
        WasPlayed = false,
        PlayoutIntent = AnnouncementPlayoutIntent.Immediate,
    };

    private static TalkBreak TalkBreak(Guid announcementId) => new()
    {
        Id = Guid.NewGuid(),
        AnnouncementId = announcementId,
        ModeratorId = 1,
        Priority = TalkBreakPriority.High,
        Status = TalkBreakStatus.Rendered,
        Purpose = "ImmediateHigh",
        Title = "ImmediateHigh",
        CreatedAtUtc = Now.AddMinutes(-5),
        RenderedAtUtc = Now.AddMinutes(-5),
        ExpiresAtUtc = Now.AddMinutes(10),
        DurationSeconds = 10,
    };

    private sealed class ConcurrentFakePlayoutQueue : IPlayoutQueue
    {
        private readonly object _lock = new();
        private readonly List<PlayoutItem> _items = [];

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        public void Enqueue(PlayoutItem item)
        {
            lock (_lock)
            {
                _items.Add(item);
            }
        }

        public void EnqueueFront(PlayoutItem item)
        {
            lock (_lock)
            {
                _items.Insert(0, item);
            }
        }

        public PlayoutItem? PeekNext()
        {
            lock (_lock)
            {
                return _items.FirstOrDefault();
            }
        }

        public Task<PlayoutItem> DequeueAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                var item = _items[0];
                _items.RemoveAt(0);
                return Task.FromResult(item);
            }
        }

        public List<Guid> SnapshotIds()
        {
            lock (_lock)
            {
                return _items.Select(item => item.ItemId).ToList();
            }
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
