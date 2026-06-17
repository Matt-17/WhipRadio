using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

[TestClass]
public class MixPlannerTests
{
    private static readonly MixerSettings Settings = new();

    private static ItemInfo Talk(double duration = 15, MediaAnalysis? analysis = null)
        => new(PlayoutItemType.Announcement, analysis, duration);

    private static ItemInfo Song(double duration = 240, MediaAnalysis? analysis = null)
        => new(PlayoutItemType.Track, analysis, duration);

    private static MediaAnalysis FullAnalysis(
        double bpm = 128, double bpmConf = 0.9, double introEnd = 14, double introConf = 0.8,
        double outroStart = 220, double outroConf = 0.7) => new()
    {
        Bpm = bpm,
        BpmConfidence = bpmConf,
        BeatGridJson = "[0.5,1.0,1.5]",
        IntroEndSeconds = introEnd,
        IntroConfidence = introConf,
        OutroStartSeconds = outroStart,
        OutroConfidence = outroConf,
        IntegratedLufs = -18,
        DurationSeconds = 240,
    };

    private static MixPlanner Planner(int seed = 1) => new(new SystemRandomSource(seed));

    [TestMethod]
    public void TalkToTalk_IsAlwaysHardCut_WithGapInConfiguredRange()
    {
        var planner = Planner();
        for (var i = 0; i < 50; i++)
        {
            var plan = planner.Plan(Talk(), Talk(), Settings);
            Assert.Equal(MixStrategy.HardCut, plan.Strategy);
            Assert.InRange(plan.GapMs, Settings.HardCutGapAfterTalkMsMin, Settings.HardCutGapAfterTalkMsMax);
            Assert.Equal(0, plan.OverlapSeconds);
        }
    }

    [TestMethod]
    public void NullAnalysis_DegradesToHardCutOrEnergyFade_NeverThrows()
    {
        var planner = Planner();
        for (var i = 0; i < 100; i++)
        {
            var plan = planner.Plan(Song(analysis: null), Song(analysis: null), Settings);
            Assert.True(plan.Strategy is MixStrategy.HardCut or MixStrategy.EnergyFade);
        }
    }

    [TestMethod]
    public void SongToSong_ShortTracks_ExcludeEnergyFade()
    {
        var planner = Planner();
        // 8 s tracks < 2 × 5 s crossfade → EnergyFade ineligible; no analysis → only HardCut.
        for (var i = 0; i < 50; i++)
        {
            var plan = planner.Plan(Song(8, analysis: null), Song(8, analysis: null), Settings);
            Assert.Equal(MixStrategy.HardCut, plan.Strategy);
            Assert.InRange(plan.GapMs, Settings.HardCutGapSongMsMin, Settings.HardCutGapSongMsMax);
        }
    }

    [TestMethod]
    public void BeatAlignedFade_RequiresBpmWithinTolerance()
    {
        var outgoing = Song(analysis: FullAnalysis(bpm: 128));
        var atTolerance = Song(analysis: FullAnalysis(bpm: 128 * 1.049)); // just inside 5 %
        var beyond = Song(analysis: FullAnalysis(bpm: 128 * 1.06));

        var planner = Planner();
        var seenAligned = false;
        for (var i = 0; i < 300; i++)
        {
            seenAligned |= planner.Plan(outgoing, atTolerance, Settings).Strategy == MixStrategy.BeatAlignedFade;
            Assert.NotEqual(MixStrategy.BeatAlignedFade, planner.Plan(outgoing, beyond, Settings).Strategy);
        }

        Assert.True(seenAligned, "BeatAlignedFade never chosen at exactly the tolerance boundary");
    }

    [TestMethod]
    public void BeatAlignedFade_RequiresConfidenceAndGrids()
    {
        var lowConfidence = FullAnalysis(bpmConf: 0.5);
        var noGrid = FullAnalysis();
        noGrid.BeatGridJson = null;

        var planner = Planner();
        for (var i = 0; i < 100; i++)
        {
            Assert.NotEqual(MixStrategy.BeatAlignedFade,
                planner.Plan(Song(analysis: lowConfidence), Song(analysis: FullAnalysis()), Settings).Strategy);
            Assert.NotEqual(MixStrategy.BeatAlignedFade,
                planner.Plan(Song(analysis: noGrid), Song(analysis: FullAnalysis()), Settings).Strategy);
        }
    }

    [TestMethod]
    public void IntroTalkOver_RequiresConfidenceAndIntroLength()
    {
        var planner = Planner();
        var goodIntro = Song(analysis: FullAnalysis(introEnd: 14, introConf: 0.8));
        var lowConfidence = Song(analysis: FullAnalysis(introConf: 0.4));
        var tooShortIntro = Song(analysis: FullAnalysis(introEnd: 5, introConf: 0.9));

        var seen = false;
        for (var i = 0; i < 300; i++)
        {
            seen |= planner.Plan(Talk(15), goodIntro, Settings).Strategy == MixStrategy.IntroTalkOver;
            Assert.NotEqual(MixStrategy.IntroTalkOver, planner.Plan(Talk(15), lowConfidence, Settings).Strategy);
            // intro 5 s < talk 15 s × 0.5 → ineligible
            Assert.NotEqual(MixStrategy.IntroTalkOver, planner.Plan(Talk(15), tooShortIntro, Settings).Strategy);
        }

        Assert.True(seen);
    }

    [TestMethod]
    public void IntroTalkOver_FillsTalkStartOffset()
    {
        // Force the pick by zeroing HardCut weight.
        var settings = Settings with
        {
            StrategyWeightsJson = """{"TalkToSong": {"HardCut": 0, "IntroTalkOver": 100}}""",
        };
        var plan = Planner().Plan(Talk(10), Song(analysis: FullAnalysis(introEnd: 20)), settings);

        Assert.Equal(MixStrategy.IntroTalkOver, plan.Strategy);
        Assert.NotNull(plan.IncomingStartOffsetSeconds);
        Assert.Equal(TransitionMath.TalkStartInSong(20, 10, settings.PostHitSafetyMs),
            plan.IncomingStartOffsetSeconds!.Value, 3);
    }

    [TestMethod]
    public void OutroTalkOver_RequiresOutroConfidence()
    {
        var planner = Planner();
        var weak = Song(analysis: FullAnalysis(outroConf: 0.4));
        for (var i = 0; i < 100; i++)
        {
            Assert.NotEqual(MixStrategy.OutroTalkOver, planner.Plan(weak, Talk(), Settings).Strategy);
        }
    }

    [TestMethod]
    public void WeightTable_RespectedOverManyDraws()
    {
        // SongToSong with full analysis: all four strategies eligible at
        // 20/25/25/30 — χ² style sanity within ±3 percentage points.
        var outgoing = Song(analysis: FullAnalysis(bpm: 128));
        var incoming = Song(analysis: FullAnalysis(bpm: 129));
        var planner = Planner(seed: 99);

        var counts = new Dictionary<MixStrategy, int>();
        const int draws = 10_000;
        for (var i = 0; i < draws; i++)
        {
            var strategy = planner.Plan(outgoing, incoming, Settings).Strategy;
            counts[strategy] = counts.GetValueOrDefault(strategy) + 1;
        }

        Assert.InRange(counts[MixStrategy.HardCut] / (double)draws, 0.17, 0.23);
        Assert.InRange(counts[MixStrategy.EnergyFade] / (double)draws, 0.22, 0.28);
        Assert.InRange(counts[MixStrategy.OutroBridgeIn] / (double)draws, 0.22, 0.28);
        Assert.InRange(counts[MixStrategy.BeatAlignedFade] / (double)draws, 0.27, 0.33);
    }

    [TestMethod]
    public void CustomWeights_OverrideDefaults()
    {
        var settings = Settings with
        {
            StrategyWeightsJson = """{"SongToSong": {"HardCut": 100, "EnergyFade": 0, "OutroBridgeIn": 0, "BeatAlignedFade": 0}}""",
        };
        var planner = Planner();
        for (var i = 0; i < 100; i++)
        {
            var plan = planner.Plan(Song(analysis: FullAnalysis()), Song(analysis: FullAnalysis()), settings);
            Assert.Equal(MixStrategy.HardCut, plan.Strategy);
        }
    }

    [TestMethod]
    public void InvalidWeightsJson_FallsBackToDefaults_NeverThrows()
    {
        var settings = Settings with { StrategyWeightsJson = "{not json!" };
        var plan = Planner().Plan(Talk(), Talk(), settings);
        Assert.Equal(MixStrategy.HardCut, plan.Strategy);
    }

    [TestMethod]
    public void ReasonTrace_IsHumanReadable()
    {
        var plan = Planner().Plan(Song(analysis: FullAnalysis(bpm: 128)), Song(analysis: FullAnalysis(bpm: 130)), Settings);
        Assert.Contains("SongToSong", plan.ReasonTrace);
        Assert.Contains("eligible=[", plan.ReasonTrace);
        Assert.Contains("picked=", plan.ReasonTrace);
        Assert.Contains("dBPM=", plan.ReasonTrace);
    }

    [TestMethod]
    public void SeededPlanner_IsDeterministic()
    {
        var outgoing = Song(analysis: FullAnalysis());
        var incoming = Song(analysis: FullAnalysis(bpm: 127));
        var a = Planner(seed: 5);
        var b = Planner(seed: 5);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(a.Plan(outgoing, incoming, Settings).Strategy, b.Plan(outgoing, incoming, Settings).Strategy);
        }
    }

    [TestMethod]
    public void TalkativeHost_GetsMoreTalkOvers()
    {
        var song = Song(analysis: FullAnalysis(introEnd: 20, introConf: 0.9));

        int CountTalkOvers(double talkativeness, int seed)
        {
            var planner = Planner(seed);
            var talk = Talk(10) with { HostTalkativeness = talkativeness };
            var count = 0;
            for (var i = 0; i < 2000; i++)
            {
                if (planner.Plan(talk, song, Settings).Strategy == MixStrategy.IntroTalkOver)
                {
                    count++;
                }
            }

            return count;
        }

        var quiet = CountTalkOvers(0.0, seed: 11);   // weight 60 × 0.5 = 30 vs 40 → ~43 %
        var chatty = CountTalkOvers(1.0, seed: 11);  // weight 60 × 1.5 = 90 vs 40 → ~69 %

        Assert.True(chatty > quiet + 300,
            $"chatty host should ride the intro far more often (quiet={quiet}, chatty={chatty})");
        Assert.InRange(quiet / 2000.0, 0.35, 0.52);
        Assert.InRange(chatty / 2000.0, 0.61, 0.77);
    }

    [TestMethod]
    public void HostInfluence_AppearsInTrace()
    {
        var talk = Talk(10) with { HostTalkativeness = 0.8 };
        var song = Song(analysis: FullAnalysis(introEnd: 20, introConf: 0.9));

        var plan = Planner().Plan(talk, song, Settings);

        Assert.Contains("talk=0.80", plan.ReasonTrace);
    }

    [TestMethod]
    public void SongToTalk_IncomingHostTalkativeness_Applies()
    {
        var song = Song(analysis: FullAnalysis(outroConf: 0.9));
        var chattyTalk = Talk(12) with { HostTalkativeness = 1.0 };

        var planner = Planner(seed: 3);
        var talkOvers = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (planner.Plan(song, chattyTalk, Settings).Strategy == MixStrategy.OutroTalkOver)
            {
                talkOvers++;
            }
        }

        // weight 45 × 1.5 ≈ 68 vs HardCut 55 → ~55 %
        Assert.InRange(talkOvers / 2000.0, 0.47, 0.63);
    }

    [TestMethod]
    public void WeightsValidation_AcceptsGoodRejectsBad()
    {
        Assert.True(MixPlanner.TryValidateWeightsJson("", out _));
        Assert.True(MixPlanner.TryValidateWeightsJson("""{"SongToSong": {"HardCut": 50}}""", out _));

        Assert.False(MixPlanner.TryValidateWeightsJson("{bad", out var error1));
        Assert.NotNull(error1);
        Assert.False(MixPlanner.TryValidateWeightsJson("""{"NotAKind": {"HardCut": 1}}""", out var error2));
        Assert.Contains("NotAKind", error2);
        Assert.False(MixPlanner.TryValidateWeightsJson("""{"SongToSong": {"Sparkle": 1}}""", out var error3));
        Assert.Contains("Sparkle", error3);
        Assert.False(MixPlanner.TryValidateWeightsJson("""{"SongToSong": {"HardCut": -5}}""", out var error4));
        Assert.Contains("Negative", error4);
    }
}
