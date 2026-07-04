using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class LongFormatNewsSchedulerTests
{
    [TestMethod]
    public void ParseAirTimes_TrimsDedupesAndSorts()
    {
        var times = LongFormatNewsScheduler.ParseAirTimes(" 20:00, 8:00 ,08:00,,not-a-time,25:99 ");
        Assert.Equal(new[] { new TimeOnly(8, 0), new TimeOnly(20, 0) }, times);
    }

    [TestMethod]
    public void ParseAirTimes_GarbageAndEmpty_YieldNoTimes()
    {
        Assert.Equal(0, LongFormatNewsScheduler.ParseAirTimes(null).Count);
        Assert.Equal(0, LongFormatNewsScheduler.ParseAirTimes("").Count);
        Assert.Equal(0, LongFormatNewsScheduler.ParseAirTimes("half past nine, 99:99").Count);
    }

    [TestMethod]
    public void FormatAirTimes_RoundTripsCanonicalForm()
    {
        var times = LongFormatNewsScheduler.ParseAirTimes("20:00,8:00");
        Assert.Equal("08:00,20:00", LongFormatNewsScheduler.FormatAirTimes(times));
    }

    [TestMethod]
    public void NormalizeDurationMinutes_ClampsToGridBounds()
    {
        Assert.Equal(30, LongFormatNewsScheduler.NormalizeDurationMinutes(5));
        Assert.Equal(30, LongFormatNewsScheduler.NormalizeDurationMinutes(0));
        Assert.Equal(30, LongFormatNewsScheduler.NormalizeDurationMinutes(-10));
        Assert.Equal(45, LongFormatNewsScheduler.NormalizeDurationMinutes(45));
        Assert.Equal(60, LongFormatNewsScheduler.NormalizeDurationMinutes(240));
    }

    [TestMethod]
    public void NextTarget_PicksTheNextTimeToday()
    {
        var airTimes = LongFormatNewsScheduler.ParseAirTimes("08:00,20:00");
        var localNow = new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.FromHours(2));

        var next = LongFormatNewsScheduler.NextTarget(localNow, airTimes);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 20, 0, 0, TimeSpan.FromHours(2)), next!.Value);
    }

    [TestMethod]
    public void NextTarget_AtTheExactAirTime_ReturnsThatTime()
    {
        var airTimes = LongFormatNewsScheduler.ParseAirTimes("08:00");
        var localNow = new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(localNow, LongFormatNewsScheduler.NextTarget(localNow, airTimes)!.Value);
    }

    [TestMethod]
    public void NextTarget_WrapsPastMidnight()
    {
        var airTimes = LongFormatNewsScheduler.ParseAirTimes("08:00,20:00");
        var localNow = new DateTimeOffset(2026, 7, 4, 21, 15, 0, TimeSpan.FromHours(-5));

        var next = LongFormatNewsScheduler.NextTarget(localNow, airTimes);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 8, 0, 0, TimeSpan.FromHours(-5)), next!.Value);
    }

    [TestMethod]
    public void NextTarget_NoAirTimes_ReturnsNull()
    {
        Assert.Null(LongFormatNewsScheduler.NextTarget(DateTimeOffset.Now, []));
    }

    [TestMethod]
    public void IsAirTime_MatchesExactMinuteOnly()
    {
        var airTimes = LongFormatNewsScheduler.ParseAirTimes("08:00,20:00");
        Assert.True(LongFormatNewsScheduler.IsAirTime(
            new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.Zero), airTimes));
        Assert.False(LongFormatNewsScheduler.IsAirTime(
            new DateTimeOffset(2026, 7, 4, 8, 1, 0, TimeSpan.Zero), airTimes));
        Assert.False(LongFormatNewsScheduler.IsAirTime(
            new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero), airTimes));
    }
}
