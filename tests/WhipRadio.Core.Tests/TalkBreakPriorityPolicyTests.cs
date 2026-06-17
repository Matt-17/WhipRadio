using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TalkBreakPriorityPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void IsOnDemandPriority_RequiresRenderedHighOrEmergencyUnexpiredBreak()
    {
        var talkBreak = Break(TalkBreakPriority.High, Now.AddMinutes(-5));

        Assert.True(TalkBreakPriorityPolicy.IsOnDemandPriority(talkBreak, Now));

        talkBreak.Priority = TalkBreakPriority.Normal;
        Assert.False(TalkBreakPriorityPolicy.IsOnDemandPriority(talkBreak, Now));

        talkBreak.Priority = TalkBreakPriority.Emergency;
        talkBreak.ExpiresAtUtc = Now.AddSeconds(-1);
        Assert.False(TalkBreakPriorityPolicy.IsOnDemandPriority(talkBreak, Now));
    }

    [TestMethod]
    public void OrderForFrontPush_PutsOldestEmergencyAtFinalFront()
    {
        var high = Break(TalkBreakPriority.High, Now.AddMinutes(-20));
        var newEmergency = Break(TalkBreakPriority.Emergency, Now.AddMinutes(-2));
        var oldEmergency = Break(TalkBreakPriority.Emergency, Now.AddMinutes(-10));

        var ordered = TalkBreakPriorityPolicy.OrderForFrontPush([oldEmergency, high, newEmergency]);

        Assert.Equal(high.Id, ordered[0].Id);
        Assert.Equal(newEmergency.Id, ordered[1].Id);
        Assert.Equal(oldEmergency.Id, ordered[2].Id);
    }

    private static TalkBreak Break(TalkBreakPriority priority, DateTime createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            AnnouncementId = Guid.NewGuid(),
            Priority = priority,
            Status = TalkBreakStatus.Rendered,
            CreatedAtUtc = createdAt,
            ExpiresAtUtc = Now.AddMinutes(10),
        };
}
