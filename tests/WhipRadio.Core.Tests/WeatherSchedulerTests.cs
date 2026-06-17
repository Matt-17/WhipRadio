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
}
