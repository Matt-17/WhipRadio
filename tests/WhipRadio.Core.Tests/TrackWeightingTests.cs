using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TrackWeightingTests
{
    [TestMethod]
    public void Weight_FreshTrack_IsOne()
    {
        Assert.Equal(1.0, TrackWeighting.Weight(upVotes: 0, downVotes: 0, playCount: 0), precision: 10);
    }

    [TestMethod]
    public void Weight_UpVotesIncreaseWeight()
    {
        Assert.Equal(2.0, TrackWeighting.Weight(upVotes: 2, downVotes: 0, playCount: 0), precision: 10);
    }

    [TestMethod]
    public void Weight_HeavyDownvotes_ClampsToFloor()
    {
        // 1 + 0.5*0 - 0.7*10 = -6 → clamped to 0.1
        Assert.Equal(0.1, TrackWeighting.Weight(upVotes: 0, downVotes: 10, playCount: 0), precision: 10);
    }

    [TestMethod]
    public void Weight_PlayCountReducesWeight()
    {
        // 1 * 1/(1 + 10*0.15) = 1/2.5 = 0.4
        Assert.Equal(0.4, TrackWeighting.Weight(upVotes: 0, downVotes: 0, playCount: 10), precision: 10);
    }

    [TestMethod]
    public void Weight_CombinesVotesAndPlayFatigue()
    {
        // votes: 1 + 0.5*4 - 0.7*1 = 2.3 ; fatigue: 1/(1+2*0.15) = 1/1.3
        Assert.Equal(2.3 / 1.3, TrackWeighting.Weight(upVotes: 4, downVotes: 1, playCount: 2), precision: 10);
    }

    [TestMethod]
    public void Weight_ConfigurableFatigueFactorSteepensDecay()
    {
        // With a steeper factor (0.3 instead of 0.15), play count 10 fades faster.
        var standard = TrackWeighting.Weight(0, 0, 10, 0.15);   // 1/2.5 = 0.4
        var steep = TrackWeighting.Weight(0, 0, 10, 0.3);       // 1/4.0 = 0.25
        Assert.Equal(0.4, standard, precision: 10);
        Assert.Equal(0.25, steep, precision: 10);
        Assert.True(steep < standard);
    }

    [TestMethod]
    public void Weight_TrackOverloadHonorsFatigueFactor()
    {
        var track = new Track { PlayCount = 10 };
        Assert.Equal(0.25, TrackWeighting.Weight(track, 0.3), precision: 10);
    }

    [TestMethod]
    [DataRow(0, 5, true)]   // 5 >= 5 and 5 > 0
    [DataRow(2, 5, true)]   // 5 > 4
    [DataRow(3, 5, false)]  // 5 <= 6
    [DataRow(0, 4, false)]  // below absolute threshold
    [DataRow(10, 21, true)] // 21 > 20
    [DataRow(10, 20, false)]
    public void ShouldRetire_FollowsRule(int upVotes, int downVotes, bool expected)
    {
        Assert.Equal(expected, TrackWeighting.ShouldRetire(upVotes, downVotes));
    }
}
