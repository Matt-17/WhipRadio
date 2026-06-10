using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

public class WeatherSchedulerTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(30, false)]
    [InlineData(59, false)]
    public void IsAirWindow_OnlyFirstTenMinutesOfTheHour(int minute, bool expected)
    {
        Assert.Equal(expected, WeatherScheduler.IsAirWindow(minute));
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(59, true)]
    [InlineData(0, false)]
    public void ShouldPrepare_OnlyLastTenMinutesOfTheHour(int minute, bool expected)
    {
        Assert.Equal(expected, WeatherScheduler.ShouldPrepare(minute));
    }
}
