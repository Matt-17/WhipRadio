using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Tests;

public class TrackWeightingTests
{
    [Fact]
    public void Weight_FreshTrack_IsOne()
    {
        Assert.Equal(1.0, TrackWeighting.Weight(upVotes: 0, downVotes: 0, playCount: 0), precision: 10);
    }

    [Fact]
    public void Weight_UpVotesIncreaseWeight()
    {
        Assert.Equal(2.0, TrackWeighting.Weight(upVotes: 2, downVotes: 0, playCount: 0), precision: 10);
    }

    [Fact]
    public void Weight_HeavyDownvotes_ClampsToFloor()
    {
        // 1 + 0.5*0 - 0.7*10 = -6 → clamped to 0.1
        Assert.Equal(0.1, TrackWeighting.Weight(upVotes: 0, downVotes: 10, playCount: 0), precision: 10);
    }

    [Fact]
    public void Weight_PlayCountReducesWeight()
    {
        // 1 * 1/(1 + 10*0.15) = 1/2.5 = 0.4
        Assert.Equal(0.4, TrackWeighting.Weight(upVotes: 0, downVotes: 0, playCount: 10), precision: 10);
    }

    [Fact]
    public void Weight_CombinesVotesAndPlayFatigue()
    {
        // votes: 1 + 0.5*4 - 0.7*1 = 2.3 ; fatigue: 1/(1+2*0.15) = 1/1.3
        Assert.Equal(2.3 / 1.3, TrackWeighting.Weight(upVotes: 4, downVotes: 1, playCount: 2), precision: 10);
    }

    [Theory]
    [InlineData(0, 5, true)]   // 5 >= 5 and 5 > 0
    [InlineData(2, 5, true)]   // 5 > 4
    [InlineData(3, 5, false)]  // 5 <= 6
    [InlineData(0, 4, false)]  // below absolute threshold
    [InlineData(10, 21, true)] // 21 > 20
    [InlineData(10, 20, false)]
    public void ShouldRetire_FollowsRule(int upVotes, int downVotes, bool expected)
    {
        Assert.Equal(expected, TrackWeighting.ShouldRetire(upVotes, downVotes));
    }
}
