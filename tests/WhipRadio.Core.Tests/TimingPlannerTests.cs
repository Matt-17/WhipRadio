using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TimingPlannerTests
{
    private static readonly DateTime Now = new(2026, 7, 4, 11, 30, 0, DateTimeKind.Utc);

    private static TimingPlannerInput Input(
        double? secondsToTarget,
        double queuedAhead = 0,
        double maxTrack = 300,
        double minTrack = 150,
        IReadOnlyList<JingleCandidate>? jingles = null,
        int introGrace = 60,
        int finishGrace = 60)
        => new(
            Now,
            secondsToTarget is { } s ? Now.AddSeconds(s) : null,
            queuedAhead,
            maxTrack,
            minTrack,
            jingles ?? [],
            introGrace,
            finishGrace);

    private static JingleCandidate Jingle(double duration, DateTime? lastUsed = null, string label = "Sting")
        => new(Guid.NewGuid(), $"library/jingles/{label}.wav", label, duration, lastUsed);

    [TestMethod]
    public void NoTarget_IsNoConstraint()
    {
        var decision = TimingPlanner.Decide(Input(null));
        Assert.Equal(TimingAction.NoConstraint, decision.Action);
        Assert.True(decision.Reason.Length > 0);
    }

    [TestMethod]
    public void FarTarget_IsNoConstraint()
    {
        // 361 s > maxTrack 300 + finishGrace 60 → any track fits.
        var decision = TimingPlanner.Decide(Input(361));
        Assert.Equal(TimingAction.NoConstraint, decision.Action);
    }

    [TestMethod]
    public void QueuedSecondsAhead_CountAgainstTheGap()
    {
        // 500 s to target but 200 s already committed → 300 s remaining → capped pick.
        var decision = TimingPlanner.Decide(Input(500, queuedAhead: 200));
        Assert.Equal(TimingAction.EnqueueTrackCapped, decision.Action);
        Assert.Equal(360.0, decision.MaxTrackDurationSeconds!.Value, precision: 3);
    }

    [TestMethod]
    public void MidGap_CapsTrackAtRemainingPlusFinishGrace()
    {
        var decision = TimingPlanner.Decide(Input(200));
        Assert.Equal(TimingAction.EnqueueTrackCapped, decision.Action);
        Assert.Equal(260.0, decision.MaxTrackDurationSeconds!.Value, precision: 3);
        Assert.True(decision.Reason.Length > 0);
    }

    [TestMethod]
    public void Cap_NeverDropsBelowMinTrackDuration()
    {
        // remaining 150 == minTrack, finishGrace 0 → cap clamps to minTrack.
        var decision = TimingPlanner.Decide(Input(150, finishGrace: 0));
        Assert.Equal(TimingAction.EnqueueTrackCapped, decision.Action);
        Assert.True(decision.MaxTrackDurationSeconds!.Value >= 150);
    }

    [TestMethod]
    public void InsideIntroGrace_WaitsForPackage()
    {
        var decision = TimingPlanner.Decide(Input(45, jingles: [Jingle(10)]));
        Assert.Equal(TimingAction.WaitForPackage, decision.Action);
        Assert.Contains("intro grace", decision.Reason);
    }

    [TestMethod]
    public void PastTarget_WaitsForPackage()
    {
        var decision = TimingPlanner.Decide(Input(-30, jingles: [Jingle(10)]));
        Assert.Equal(TimingAction.WaitForPackage, decision.Action);
    }

    [TestMethod]
    public void SmallGap_BridgesWithBestFitJingle()
    {
        var shortSting = Jingle(8, label: "Short");
        var longSting = Jingle(20, label: "Long");
        // 100 s remaining < minTrack 150 and > introGrace 60 → jingle fill, longest fitting first.
        var decision = TimingPlanner.Decide(Input(100, jingles: [shortSting, longSting]));
        Assert.Equal(TimingAction.EnqueueJingleFill, decision.Action);
        Assert.Equal(longSting.Id, decision.Jingle!.Id);
    }

    [TestMethod]
    public void JingleFill_RotatesByLastUsed()
    {
        var usedRecently = Jingle(15, Now.AddMinutes(-5), label: "Fresh");
        var usedLongAgo = Jingle(15, Now.AddHours(-9), label: "Rested");
        var neverUsed = Jingle(15, null, label: "New");

        var decision = TimingPlanner.Decide(Input(100, jingles: [usedRecently, usedLongAgo, neverUsed]));
        Assert.Equal(TimingAction.EnqueueJingleFill, decision.Action);
        Assert.Equal(neverUsed.Id, decision.Jingle!.Id);
    }

    [TestMethod]
    public void JingleLongerThanGapPlusGrace_IsNotEligible()
    {
        // 100 s gap + 60 s intro grace = 160 s budget; a 170 s jingle can't fit.
        var oversized = Jingle(170, label: "Oversized");
        var decision = TimingPlanner.Decide(Input(100, jingles: [oversized]));
        Assert.Equal(TimingAction.WaitForPackage, decision.Action);
        Assert.Contains("no jingle fits", decision.Reason);
    }

    [TestMethod]
    public void NoJingles_SmallGap_FallsBackToWait()
    {
        var decision = TimingPlanner.Decide(Input(100));
        Assert.Equal(TimingAction.WaitForPackage, decision.Action);
        Assert.Contains("fade", decision.Reason);
    }

    [TestMethod]
    public void AfterUnfitTrackPick_SkipsTheTrackOption()
    {
        var sting = Jingle(20);
        var input = Input(200, jingles: [sting]);

        Assert.Equal(TimingAction.EnqueueTrackCapped, TimingPlanner.Decide(input).Action);

        var fallback = TimingPlanner.DecideAfterUnfitTrackPick(input);
        Assert.Equal(TimingAction.EnqueueJingleFill, fallback.Action);
        Assert.Equal(sting.Id, fallback.Jingle!.Id);
    }

    [TestMethod]
    public void AfterUnfitTrackPick_NoJingle_Waits()
    {
        var fallback = TimingPlanner.DecideAfterUnfitTrackPick(Input(200));
        Assert.Equal(TimingAction.WaitForPackage, fallback.Action);
    }

    [TestMethod]
    public void EveryDecision_CarriesAReason()
    {
        TimingPlannerInput[] inputs =
        [
            Input(null),
            Input(1000),
            Input(200),
            Input(100, jingles: [Jingle(15)]),
            Input(100),
            Input(30),
        ];

        foreach (var input in inputs)
        {
            var decision = TimingPlanner.Decide(input);
            Assert.True(decision.Reason.Length > 0, $"missing reason for {decision.Action}");
        }
    }

    [TestMethod]
    public void RemainingSeconds_SubtractsQueuedAhead()
    {
        Assert.Equal(120.0, TimingPlanner.RemainingSecondsBeforeTarget(Input(300, queuedAhead: 180)), precision: 3);
        Assert.True(double.IsPositiveInfinity(TimingPlanner.RemainingSecondsBeforeTarget(Input(null))));
    }
}
