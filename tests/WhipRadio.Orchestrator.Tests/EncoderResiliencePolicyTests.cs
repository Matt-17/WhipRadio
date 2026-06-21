using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class EncoderResiliencePolicyTests
{
    private static readonly DateTime Start = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);

    // Defaults mirror StreamOptions: 5m window, threshold 5, 5s→60s backoff, 120s success reset.
    private static EncoderResiliencePolicy NewPolicy(DateTime now) => new(
        window: TimeSpan.FromMinutes(5),
        threshold: 5,
        initialBackoff: TimeSpan.FromSeconds(5),
        maxBackoff: TimeSpan.FromSeconds(60),
        successResetsAfter: TimeSpan.FromSeconds(120),
        nowUtc: now);

    [TestMethod]
    public void Backoff_GrowsExponentiallyThenCaps()
    {
        var policy = NewPolicy(Start);

        // Crash 1 → 5s, 2 → 10s, 3 → 20s, 4 → 40s, 5 → 60s (cap), 6 → 60s.
        var expected = new[] { 5, 10, 20, 40, 60, 60 };
        for (var i = 0; i < expected.Length; i++)
        {
            // Each crash happens immediately (hot-loop): session ran ~0s.
            policy.MarkSessionStart(Start.AddSeconds(i));
            var tripped = policy.RecordCrash(Start.AddSeconds(i));
            Assert.Equal(expected[i], policy.NextBackoff().TotalSeconds, 0);
            if (i < 4)
            {
                Assert.False(tripped, $"breaker should not trip before threshold (crash {i + 1})");
            }
            else
            {
                Assert.True(tripped, $"breaker should trip at/after threshold (crash {i + 1})");
            }
        }
    }

    [TestMethod]
    public void RecordCrash_TripsBreakerAtThresholdInsideWindow()
    {
        var policy = NewPolicy(Start);

        for (var i = 0; i < 4; i++)
        {
            policy.MarkSessionStart(Start.AddSeconds(i));
            Assert.False(policy.RecordCrash(Start.AddSeconds(i)), $"crash {i + 1} must not trip");
        }

        policy.MarkSessionStart(Start.AddSeconds(4));
        Assert.True(policy.RecordCrash(Start.AddSeconds(4)), "5th crash inside the window must trip the breaker");
        Assert.Equal(5, policy.CrashesInWindow);
    }

    [TestMethod]
    public void OldCrashesPruneOutOfTheWindowSoBreakerDoesNotTrip()
    {
        var policy = NewPolicy(Start);

        // 4 crashes close together, then a 5th crash well outside the 5-minute window.
        for (var i = 0; i < 4; i++)
        {
            policy.MarkSessionStart(Start.AddSeconds(i));
            policy.RecordCrash(Start.AddSeconds(i));
        }

        var muchLater = Start.AddMinutes(10);
        policy.MarkSessionStart(muchLater);
        Assert.False(policy.RecordCrash(muchLater), "stale crashes must be pruned — breaker should not trip");
        Assert.Equal(1, policy.CrashesInWindow);
    }

    [TestMethod]
    public void LongHealthySessionClearsCrashesBeforeRecordingTheNext()
    {
        var policy = NewPolicy(Start);

        // Prime the window with 3 rapid crashes.
        for (var i = 0; i < 3; i++)
        {
            policy.MarkSessionStart(Start.AddSeconds(i));
            policy.RecordCrash(Start.AddSeconds(i));
        }
        Assert.Equal(3, policy.CrashesInWindow);

        // A session that survives past successResetsAfter (120s) then crashes is a
        // fresh incident: the window clears and the breaker is not primed.
        var later = Start.AddSeconds(200);
        policy.MarkSessionStart(Start); // session started 200s before the crash
        Assert.False(policy.RecordCrash(later), "long session should reset the streak before counting the crash");
        Assert.Equal(1, policy.CrashesInWindow);
        Assert.Equal(5, policy.NextBackoff().TotalSeconds, 0); // backoff back to the floor
    }

    [TestMethod]
    public void ResetClearsTheWindow()
    {
        var policy = NewPolicy(Start);
        for (var i = 0; i < 3; i++)
        {
            policy.MarkSessionStart(Start.AddSeconds(i));
            policy.RecordCrash(Start.AddSeconds(i));
        }

        policy.Reset();
        Assert.Equal(0, policy.CrashesInWindow);
        Assert.Equal(5, policy.NextBackoff().TotalSeconds, 0);
    }

    [TestMethod]
    public void ThresholdOf1TripsOnFirstCrash()
    {
        var policy = new EncoderResiliencePolicy(
            window: TimeSpan.FromMinutes(5),
            threshold: 1,
            initialBackoff: TimeSpan.FromSeconds(5),
            maxBackoff: TimeSpan.FromSeconds(60),
            successResetsAfter: TimeSpan.FromSeconds(120),
            nowUtc: Start);

        policy.MarkSessionStart(Start);
        Assert.True(policy.RecordCrash(Start));
    }
}
