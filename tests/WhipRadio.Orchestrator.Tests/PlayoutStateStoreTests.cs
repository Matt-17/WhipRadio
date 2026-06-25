using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class PlayoutStateStoreTests
{
    private static readonly DateTime Start = new(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void BuildResumePlan_RestoresCurrentItemWithElapsedOffsetAndSameQueue()
    {
        var root = TestRoot();
        try
        {
            var current = Item("current", 180);
            var next = Item("next", 210);
            var weather = Item("weather", 45, PlayoutItemType.Announcement);
            var writerTime = new MutableTimeProvider(Start);
            var writer = CreateStore(root, writerTime);
            writer.Enqueued(current);
            writer.Enqueued(next);
            writer.Enqueued(weather);
            writer.MarkStarted(current);

            var readerTime = new MutableTimeProvider(Start.AddSeconds(42));
            var plan = CreateStore(root, readerTime).BuildResumePlan();

            Assert.NotNull(plan.CurrentItem);
            Assert.Equal(current.ItemId, plan.CurrentItem!.ItemId);
            Assert.Equal(42, plan.CurrentItem.StartOffsetSeconds, precision: 6);
            // The rehydrated current item is flagged so the play log won't double-record it.
            Assert.True(plan.CurrentItem.IsResumed);
            Assert.Equal(new[] { next.ItemId, weather.ItemId }, plan.QueueItems.Select(item => item.ItemId).ToArray());
            // Queued items were never on air, so they must log normally on first play.
            Assert.True(plan.QueueItems.All(item => !item.IsResumed));
            Assert.Empty(plan.SkippedItems);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void BuildResumePlan_AdvancesIntoNextQueuedItemWhenDowntimeOutlastsCurrent()
    {
        var root = TestRoot();
        try
        {
            var current = Item("current", 60);
            var next = Item("next", 120);
            var weather = Item("weather", 45, PlayoutItemType.Announcement);
            var writer = CreateStore(root, new MutableTimeProvider(Start));
            writer.Enqueued(current);
            writer.Enqueued(next);
            writer.Enqueued(weather);
            writer.MarkStarted(current);

            var plan = CreateStore(root, new MutableTimeProvider(Start.AddSeconds(75))).BuildResumePlan();

            Assert.NotNull(plan.CurrentItem);
            Assert.Equal(next.ItemId, plan.CurrentItem!.ItemId);
            Assert.Equal(15, plan.CurrentItem.StartOffsetSeconds, precision: 6);
            Assert.Equal(new[] { weather.ItemId }, plan.QueueItems.Select(item => item.ItemId).ToArray());
            Assert.Equal(new[] { current.ItemId }, plan.SkippedItems.Select(item => item.ItemId).ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void BuildResumePlan_PreservesQueuedOffsetIfRestartRepeatsBeforePlaybackStarts()
    {
        var root = TestRoot();
        try
        {
            var current = Item("current", 180) with { StartOffsetSeconds = 30 };
            var next = Item("next", 120);
            var writer = CreateStore(root, new MutableTimeProvider(Start));
            writer.Enqueued(current);
            writer.Enqueued(next);

            var plan = CreateStore(root, new MutableTimeProvider(Start.AddSeconds(12))).BuildResumePlan();

            Assert.NotNull(plan.CurrentItem);
            Assert.Equal(current.ItemId, plan.CurrentItem!.ItemId);
            Assert.Equal(42, plan.CurrentItem.StartOffsetSeconds, precision: 6);
            Assert.Equal(new[] { next.ItemId }, plan.QueueItems.Select(item => item.ItemId).ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static PlayoutStateStore CreateStore(string root, TimeProvider timeProvider)
        => new(
            Options.Create(new RadioOptions { DataRoot = root }),
            timeProvider,
            NullLogger<PlayoutStateStore>.Instance);

    private static PlayoutItem Item(string title, double duration, PlayoutItemType type = PlayoutItemType.Track)
        => new(type, Guid.NewGuid(), $"library/{title}.wav", title, duration, ModeratorId: 1);

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "whipradio-playout-state-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
