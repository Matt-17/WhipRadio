using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class WeatherSchedulerTests
{
    [TestMethod]
    [DataRow(0, true)]
    [DataRow(9, true)]
    [DataRow(10, false)]
    [DataRow(30, false)]
    [DataRow(59, false)]
    public void IsAirWindow_OnlyFirstTenMinutesOfTheHour(int minute, bool expected)
    {
        Assert.Equal(expected, WeatherScheduler.IsAirWindow(minute));
    }

    [TestMethod]
    [DataRow(49, false)]
    [DataRow(50, true)]
    [DataRow(59, true)]
    [DataRow(0, false)]
    public void ShouldPrepare_OnlyLastTenMinutesOfTheHour(int minute, bool expected)
    {
        Assert.Equal(expected, WeatherScheduler.ShouldPrepare(minute));
    }

    [TestMethod]
    public void CadenceAwareScheduler_UsesConfiguredBoundary()
    {
        var beforeHalfHour = new DateTimeOffset(2026, 6, 17, 10, 15, 0, TimeSpan.Zero);
        var nearHalfHour = new DateTimeOffset(2026, 6, 17, 10, 25, 0, TimeSpan.Zero);
        var justAfterHalfHour = new DateTimeOffset(2026, 6, 17, 10, 31, 0, TimeSpan.Zero);

        Assert.False(WeatherScheduler.ShouldPrepare(beforeHalfHour, cadenceMinutes: 30));
        Assert.True(WeatherScheduler.ShouldPrepare(nearHalfHour, cadenceMinutes: 30));
        Assert.True(WeatherScheduler.IsAirWindow(justAfterHalfHour, cadenceMinutes: 30));
        Assert.Equal(
            new DateTimeOffset(2026, 6, 17, 10, 30, 0, TimeSpan.Zero),
            WeatherScheduler.CurrentWindowStart(justAfterHalfHour, cadenceMinutes: 30));
    }
}
