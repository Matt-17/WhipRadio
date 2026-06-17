using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TalkBitPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void IsEligible_RespectsCooldown()
    {
        var bit = new TalkBit
        {
            Status = TalkBitStatus.Active,
            CooldownDays = 5,
            LastUsedAtUtc = Now.AddDays(-3),
        };

        Assert.False(TalkBitPolicy.IsEligible(bit, Now));

        bit.LastUsedAtUtc = Now.AddDays(-5);

        Assert.True(TalkBitPolicy.IsEligible(bit, Now));
    }

    [TestMethod]
    public void SelectionWeight_FallsWithPlayCount()
    {
        var fresh = new TalkBit
        {
            Status = TalkBitStatus.Active,
            CooldownDays = 5,
            PlayCount = 0,
            LastUsedAtUtc = Now.AddDays(-10),
        };
        var tired = fresh.WithPlayCount(4);

        Assert.True(TalkBitPolicy.SelectionWeight(fresh, Now) > TalkBitPolicy.SelectionWeight(tired, Now));
    }

    [TestMethod]
    public void PickWeighted_IgnoresIneligibleBits()
    {
        var eligible = new TalkBit
        {
            Id = Guid.NewGuid(),
            Status = TalkBitStatus.Active,
            CooldownDays = 5,
            LastUsedAtUtc = Now.AddDays(-6),
        };
        var coolingDown = new TalkBit
        {
            Id = Guid.NewGuid(),
            Status = TalkBitStatus.Active,
            CooldownDays = 5,
            LastUsedAtUtc = Now.AddDays(-1),
        };

        var picked = TalkBitPolicy.PickWeighted([coolingDown, eligible], Now, new Random(123));

        Assert.Equal(eligible.Id, picked?.Id);
    }

    [TestMethod]
    public void ShouldForceRetelling_AfterExactReplayLimit()
    {
        Assert.False(TalkBitPolicy.ShouldForceRetelling(new TalkBit { ExactReplayCount = 1 }, exactReplayLimit: 2));
        Assert.True(TalkBitPolicy.ShouldForceRetelling(new TalkBit { ExactReplayCount = 2 }, exactReplayLimit: 2));
    }

    [TestMethod]
    public void ShouldRetire_ByPlayCountOrAge()
    {
        Assert.True(TalkBitPolicy.ShouldRetire(new TalkBit { Status = TalkBitStatus.Active, PlayCount = 12, CreatedAtUtc = Now }, utcNow: Now));
        Assert.True(TalkBitPolicy.ShouldRetire(new TalkBit { Status = TalkBitStatus.Active, CreatedAtUtc = Now.AddDays(-181) }, utcNow: Now));
        Assert.False(TalkBitPolicy.ShouldRetire(new TalkBit { Status = TalkBitStatus.Active, CreatedAtUtc = Now.AddDays(-30) }, utcNow: Now));
    }

    [TestMethod]
    public void LooksDuplicate_UsesKeywordOverlap()
    {
        var existing = new[]
        {
            new TalkBit
            {
                Status = TalkBitStatus.Active,
                Premise = "the drummer and the metronome always fighting about tempo",
            },
        };

        Assert.True(TalkBitPolicy.LooksDuplicate("metronome tempo argument with the drummer", existing));
        Assert.False(TalkBitPolicy.LooksDuplicate("a weather joke about tiny umbrellas", existing));
    }
}

file static class TalkBitTestExtensions
{
    public static TalkBit WithPlayCount(this TalkBit bit, int playCount)
        => new()
        {
            Id = bit.Id,
            ModeratorId = bit.ModeratorId,
            Premise = bit.Premise,
            Tags = bit.Tags,
            Status = bit.Status,
            CooldownDays = bit.CooldownDays,
            PlayCount = playCount,
            ExactReplayCount = bit.ExactReplayCount,
            FreshRetellCount = bit.FreshRetellCount,
            LastUsedAtUtc = bit.LastUsedAtUtc,
            CreatedAtUtc = bit.CreatedAtUtc,
        };
}
