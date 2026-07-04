using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class TimedPlayoutInterruptServiceTests
{
    [TestMethod]
    public void TryConsume_ReturnsPackageInsideIntroGraceWindow()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var item = Package();

        service.Schedule(new TimedPlayoutInterrupt(item, target, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var consumed = service.TryConsume(target.AddSeconds(-15));

        Assert.NotNull(consumed);
        Assert.Equal(item.ItemId, consumed!.Item.ItemId);
    }

    [TestMethod]
    public void TryConsume_HoldsPackageBeforeIntroGraceWindow()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        service.Schedule(new TimedPlayoutInterrupt(Package(), target, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var consumed = service.TryConsume(target.AddSeconds(-16));

        Assert.Null(consumed);
    }

    [TestMethod]
    public void TryConsume_ReturnsPackageUntilLateWindowCloses()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var item = Package();

        service.Schedule(new TimedPlayoutInterrupt(item, target, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var consumed = service.TryConsume(target.AddMinutes(5));

        Assert.NotNull(consumed);
        Assert.Equal(item.ItemId, consumed!.Item.ItemId);
    }

    [TestMethod]
    public void TryConsume_DropsPackageAfterLateWindowCloses()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        service.Schedule(new TimedPlayoutInterrupt(Package(), target, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var consumed = service.TryConsume(target.AddMinutes(5).AddSeconds(1));

        Assert.Null(consumed);
    }

    [TestMethod]
    public void TryConsume_TwoPendingInterrupts_ReleasesEachAtItsOwnTarget()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var newsTarget = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var podcastTarget = new DateTime(2026, 6, 21, 12, 30, 0, DateTimeKind.Utc);
        var news = Package();
        var podcast = Package();

        service.Schedule(new TimedPlayoutInterrupt(news, newsTarget, 1, GraceSeconds: 15, LateWindowSeconds: 300));
        service.Schedule(new TimedPlayoutInterrupt(podcast, podcastTarget, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var first = service.TryConsume(newsTarget);
        Assert.NotNull(first);
        Assert.Equal(news.ItemId, first!.Item.ItemId);

        // The podcast is not due yet — and must still be pending after the news fired.
        Assert.Null(service.TryConsume(newsTarget.AddSeconds(1)));
        Assert.True(service.HasPending(podcast.ItemId, podcastTarget));

        var second = service.TryConsume(podcastTarget);
        Assert.NotNull(second);
        Assert.Equal(podcast.ItemId, second!.Item.ItemId);
    }

    [TestMethod]
    public void TryConsume_BothDue_ReleasesEarliestTargetFirst()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var earlier = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddMinutes(2);
        var earlyItem = Package();
        var lateItem = Package();

        service.Schedule(new TimedPlayoutInterrupt(lateItem, later, 1, GraceSeconds: 15, LateWindowSeconds: 300));
        service.Schedule(new TimedPlayoutInterrupt(earlyItem, earlier, 1, GraceSeconds: 15, LateWindowSeconds: 300));

        var consumed = service.TryConsume(later); // both inside their windows now
        Assert.Equal(earlyItem.ItemId, consumed!.Item.ItemId);
    }

    [TestMethod]
    public void TargetedClear_RemovesOnlyThatItem()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var news = Package();
        var podcast = Package();

        service.Schedule(new TimedPlayoutInterrupt(news, target, 1, GraceSeconds: 15, LateWindowSeconds: 300));
        service.Schedule(new TimedPlayoutInterrupt(podcast, target.AddMinutes(30), 1, GraceSeconds: 15, LateWindowSeconds: 300));

        service.Clear(news.ItemId);

        Assert.False(service.HasPending(news.ItemId, target));
        Assert.True(service.HasPending(podcast.ItemId, target.AddMinutes(30)));
    }

    [TestMethod]
    public void WasRecentlyConsumed_TracksMultipleConsumedItems()
    {
        var service = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var target = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var first = Package();
        var second = Package();

        service.Schedule(new TimedPlayoutInterrupt(first, target, 1, GraceSeconds: 15, LateWindowSeconds: 300));
        service.Schedule(new TimedPlayoutInterrupt(second, target.AddMinutes(1), 1, GraceSeconds: 15, LateWindowSeconds: 300));
        _ = service.TryConsume(target);
        _ = service.TryConsume(target.AddMinutes(1));

        Assert.True(service.WasRecentlyConsumed(first.ItemId, target, TimeSpan.FromMinutes(5), target.AddMinutes(2)));
        Assert.True(service.WasRecentlyConsumed(second.ItemId, target.AddMinutes(1), TimeSpan.FromMinutes(5), target.AddMinutes(2)));
        Assert.False(service.WasRecentlyConsumed(first.ItemId, target, TimeSpan.FromMinutes(1), target.AddMinutes(2)));
    }

    private static PlayoutItem Package()
        => new(
            PlayoutItemType.Announcement,
            Guid.NewGuid(),
            "library/announcements/package.wav",
            "Top of hour",
            60);
}
