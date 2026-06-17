using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TalkPlannerTests
{
    [TestMethod]
    public void EffectiveTalkativeness_FormatTempersTheHost()
    {
        Assert.Equal(0.5, TalkPlanner.EffectiveTalkativeness(0.8, 0.2), precision: 10);
        Assert.Equal(0.8, TalkPlanner.EffectiveTalkativeness(0.8, null), precision: 10);
    }

    [TestMethod]
    public void EffectiveTalkativeness_UsesFormatTalkDensity()
    {
        var moderator = new Moderator { Talkativeness = 0.8 };
        var format = new Format { Talkativeness = 0.2, TalkDensity = 0.4 };

        Assert.Equal(0.6, TalkPlanner.EffectiveTalkativeness(moderator, format), precision: 10);
    }

    [TestMethod]
    public void PickGapTalkCount_QuietHostIsMostlySilent()
    {
        var random = new Random(7);
        var counts = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickGapTalkCount(random, hasMandatoryTalk: false, talkativeness: 0.0))
            .ToList();

        Assert.True(counts.Count(c => c == 0) > 250, "a quiet host should skip most gaps");
        Assert.DoesNotContain(counts, c => c > 1);
    }

    [TestMethod]
    public void PickGapTalkCount_TalkyHostChainsTalks()
    {
        var random = new Random(7);
        var counts = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickGapTalkCount(random, hasMandatoryTalk: false, talkativeness: 1.0))
            .ToList();

        Assert.True(counts.Count(c => c == 0) < 100, "a talky host is rarely silent");
        Assert.Contains(counts, c => c >= 2);
        Assert.All(counts, c => Assert.InRange(c, 0, 3));
    }

    [TestMethod]
    public void PickGapTalkCount_MandatoryTalkThinsOutFreeTalks()
    {
        var random = new Random(7);
        var counts = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickGapTalkCount(random, hasMandatoryTalk: true, talkativeness: 0.5))
            .ToList();

        Assert.True(counts.Count(c => c == 0) > 250, "weather/greeting already filled the gap");
        Assert.DoesNotContain(counts, c => c == 3);
    }

    [TestMethod]
    public void PickGapTalkCount_ProfileCanDisableFreeTalk()
    {
        var random = new Random(7);
        var moderator = new Moderator { Talkativeness = 1, TalkBreakFrequencyTracks = 0 };

        var count = TalkPlanner.PickGapTalkCount(random, hasMandatoryTalk: false, moderator, format: null);

        Assert.Equal(0, count);
    }

    [TestMethod]
    public void PickGapTalkCount_ProfileClampsPartCount()
    {
        var random = new Random(7);
        var moderator = new Moderator
        {
            Talkativeness = 1,
            TalkBreakFrequencyTracks = 1,
            MinTalkPartsPerBreak = 1,
            MaxTalkPartsPerBreak = 1,
        };

        var counts = Enumerable.Range(0, 200)
            .Select(_ => TalkPlanner.PickGapTalkCount(random, hasMandatoryTalk: false, moderator, format: null))
            .ToList();

        Assert.DoesNotContain(counts, c => c > 1);
    }

    [TestMethod]
    public void PickLengthHint_AllVariantsReachable()
    {
        var random = new Random(7);
        var hints = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickLengthHint(random, talkativeness: 0.8))
            .Distinct()
            .ToList();

        Assert.Equal(4, hints.Count); // one-liner, short, medium, story
    }

    [TestMethod]
    public void PickLengthHint_TalkDepthControlsInstruction()
    {
        var nameOnly = TalkPlanner.PickLengthHint(new Random(7), TalkDepth.NameOnly, talkativeness: 0.8);
        var deepDive = TalkPlanner.PickLengthHint(new Random(7), TalkDepth.DeepDive, talkativeness: 0.8);

        Assert.Contains("only identify", nameOnly);
        Assert.DoesNotContain("only identify", deepDive);
    }

    [TestMethod]
    public void PickGreetingBatchSize_QuietHostReadsOneAtATime()
    {
        var random = new Random(7);
        var sizes = Enumerable.Range(0, 200)
            .Select(_ => TalkPlanner.PickGreetingBatchSize(random, talkativeness: 0.0))
            .ToList();

        Assert.All(sizes, s => Assert.Equal(1, s));
    }

    [TestMethod]
    public void PickGreetingBatchSize_TalkyHostCanClearTheMailbag()
    {
        var random = new Random(7);
        var sizes = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickGreetingBatchSize(random, talkativeness: 1.0))
            .ToList();

        Assert.All(sizes, s => Assert.InRange(s, 1, 10));
        Assert.Contains(sizes, s => s >= 8); // in the mood: many greetings in one go
    }

    [TestMethod]
    public void PickFreeTalkKind_RespectsAvailableContext()
    {
        var random = new Random(7);
        var kinds = Enumerable.Range(0, 500)
            .Select(_ => TalkPlanner.PickFreeTalkKind(random, hasNextTrack: false, hasPreviousTrack: false))
            .Distinct()
            .ToList();

        Assert.DoesNotContain(AnnouncementKind.SongIntro, kinds);
        Assert.DoesNotContain(AnnouncementKind.SongOutro, kinds);
        Assert.Contains(AnnouncementKind.Banter, kinds);
    }

    [TestMethod]
    public void PickFreeTalkKind_RespectsAllowedKinds()
    {
        var random = new Random(7);
        var profile = new HostTalkProfile(
            BreakFrequencyTracks: 1,
            MinPartsPerBreak: 1,
            MaxPartsPerBreak: 3,
            AllowedKinds: new HashSet<AnnouncementKind> { AnnouncementKind.Joke },
            ExactReplayTolerance: 2,
            EvergreenBitTolerance: 0.5);

        var kind = TalkPlanner.PickFreeTalkKind(
            random,
            hasNextTrack: true,
            hasPreviousTrack: true,
            profile);

        Assert.Equal(AnnouncementKind.Joke, kind);
    }
}
