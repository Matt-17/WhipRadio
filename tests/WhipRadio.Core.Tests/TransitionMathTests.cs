using WhipRadio.Core.Audio;

namespace WhipRadio.Core.Tests;

public class TransitionMathTests
{
    private sealed class FixedRandom(params int[] values) : IRandomSource
    {
        private int _index;

        public int NextInt(int minInclusive, int maxExclusive)
            => Math.Clamp(values[_index++ % values.Length], minInclusive, maxExclusive - 1);

        public double NextDouble() => 0.5;
    }

    // --- hit the post ----------------------------------------------------------

    [Fact]
    public void TalkStart_LongIntro_TalkEndsBeforePost()
    {
        // 20 s intro, 10 s talk, 800 ms safety → talk starts at 9.2 s into the song.
        var start = TransitionMath.TalkStartInSong(20, 10, 800);
        Assert.Equal(9.2, start, 3);
        // talk ends at start + 10 = 19.2 s = IntroEnd − safety ✓
        Assert.Equal(20 - 0.8, start + 10, 3);
    }

    [Fact]
    public void TalkStart_ShortIntro_ClampsAtZero()
    {
        Assert.Equal(0, TransitionMath.TalkStartInSong(5, 10, 800));
    }

    [Fact]
    public void TalkStart_ExactFit_StartsAtZero()
    {
        Assert.Equal(0, TransitionMath.TalkStartInSong(10.8, 10, 800), 3);
    }

    [Fact]
    public void CanHitThePost_RequiresIntroAtLeastHalfTheTalk()
    {
        Assert.True(TransitionMath.CanHitThePost(introEndSeconds: 5, talkDurationSeconds: 10));
        Assert.False(TransitionMath.CanHitThePost(introEndSeconds: 4.9, talkDurationSeconds: 10));
    }

    // --- beat alignment ---------------------------------------------------------

    [Fact]
    public void NearestBeat_PicksClosest()
    {
        double[] grid = [0.5, 1.0, 1.5, 2.0];
        Assert.Equal(1.5, TransitionMath.NearestBeat(grid, 1.6));
        Assert.Equal(0.5, TransitionMath.NearestBeat(grid, 0.1));
        Assert.Equal(2.0, TransitionMath.NearestBeat(grid, 99));
    }

    [Fact]
    public void IncomingStart_OffsetsByFirstBeat()
    {
        // Outgoing beat lands at master sample 441000 (10 s); incoming's first
        // beat is 0.5 s into its file → incoming starts 0.5 s earlier.
        var start = TransitionMath.IncomingStartMasterSample(441000, 0.5, 44100);
        Assert.Equal(441000 - 22050, start);
    }

    [Fact]
    public void CrossfadeBeats_ScalesWithBpmAndClamps()
    {
        Assert.Equal(11, TransitionMath.CrossfadeBeats(5, 128)); // round(10.67) = 11
        Assert.Equal(4, TransitionMath.CrossfadeBeats(1, 60));   // min clamp
        Assert.Equal(16, TransitionMath.CrossfadeBeats(10, 180)); // max clamp
    }

    // --- gap sampling -----------------------------------------------------------

    [Fact]
    public void GapSampling_StaysInRange()
    {
        var random = new SystemRandomSource(seed: 42);
        for (var i = 0; i < 200; i++)
        {
            var gap = TransitionMath.SampleGapMs(random, 200, 600);
            Assert.InRange(gap, 200, 600);
        }
    }

    [Fact]
    public void GapSampling_DegenerateRange_ReturnsMin()
    {
        Assert.Equal(300, TransitionMath.SampleGapMs(new FixedRandom(0), 300, 300));
    }

    [Fact]
    public void GapSampling_IsDeterministicWithSeed()
    {
        var a = new SystemRandomSource(seed: 7);
        var b = new SystemRandomSource(seed: 7);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(TransitionMath.SampleGapMs(a, 0, 150), TransitionMath.SampleGapMs(b, 0, 150));
        }
    }

    // --- makeup gain -------------------------------------------------------------

    [Fact]
    public void Makeup_QuietItem_GainsUpToTarget()
    {
        // −19.3 LUFS → target −16 → +3.3 dB
        var linear = TransitionMath.MakeupGainLinear(-19.3, -16.0, 6.0);
        Assert.Equal(Math.Pow(10, 3.3 / 20), linear, 3);
    }

    [Fact]
    public void Makeup_ClampsAtMaxGain()
    {
        // −30 LUFS would need +14 dB → clamped to +6 dB
        var linear = TransitionMath.MakeupGainLinear(-30.0, -16.0, 6.0);
        Assert.Equal(Math.Pow(10, 6.0 / 20), linear, 3);
    }

    [Fact]
    public void Makeup_ClampsAtMaxAttenuation()
    {
        // −8 LUFS would need −8 dB → clamped to −6 dB
        var linear = TransitionMath.MakeupGainLinear(-8.0, -16.0, 6.0);
        Assert.Equal(Math.Pow(10, -6.0 / 20), linear, 3);
    }

    [Fact]
    public void Makeup_NoAnalysis_IsUnity()
    {
        Assert.Equal(1f, TransitionMath.MakeupGainLinear(null, -16.0, 6.0));
    }
}
