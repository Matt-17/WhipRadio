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

    private static PlayoutItem Package()
        => new(
            PlayoutItemType.Announcement,
            Guid.NewGuid(),
            "library/announcements/package.wav",
            "Top of hour",
            60);
}
