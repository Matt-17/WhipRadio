using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class NewsAirtimeRampTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Priority_FarFromAir_IsLow()
    {
        Assert.Equal(GpuJobPriority.Low, NewsAirtimeRamp.Priority(Now.AddMinutes(45), Now));
        Assert.Equal(GpuJobPriority.Low, NewsAirtimeRamp.Priority(Now.AddMinutes(20), Now));
    }

    [TestMethod]
    public void Priority_BetweenTenAndTwentyMinutes_IsMedium()
    {
        Assert.Equal(GpuJobPriority.Medium, NewsAirtimeRamp.Priority(Now.AddMinutes(19), Now));
        Assert.Equal(GpuJobPriority.Medium, NewsAirtimeRamp.Priority(Now.AddMinutes(10), Now));
    }

    [TestMethod]
    public void Priority_FinalTenMinutes_IsHighest()
    {
        Assert.Equal(GpuJobPriority.Highest, NewsAirtimeRamp.Priority(Now.AddMinutes(9), Now));
        Assert.Equal(GpuJobPriority.Highest, NewsAirtimeRamp.Priority(Now.AddMinutes(1), Now));
    }

    [TestMethod]
    public void Priority_PastTarget_StaysHighest()
        => Assert.Equal(GpuJobPriority.Highest, NewsAirtimeRamp.Priority(Now.AddMinutes(-5), Now));
}
