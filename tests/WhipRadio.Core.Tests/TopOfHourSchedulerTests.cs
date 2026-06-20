using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class TopOfHourSchedulerTests
{
    [TestMethod]
    public void NextPreparationTarget_ReturnsNextCadenceBoundaryInsidePrepareWindow()
    {
        var now = new DateTimeOffset(2026, 6, 19, 7, 52, 0, TimeSpan.Zero);

        var target = TopOfHourScheduler.NextPreparationTarget(now, cadenceMinutes: 60);

        Assert.Equal(new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero), target);
    }

    [TestMethod]
    public void NextPreparationTarget_ReturnsMinValueOutsidePrepareWindow()
    {
        var now = new DateTimeOffset(2026, 6, 19, 7, 30, 0, TimeSpan.Zero);

        var target = TopOfHourScheduler.NextPreparationTarget(now, cadenceMinutes: 60);

        Assert.Equal(DateTimeOffset.MinValue, target);
    }

    [TestMethod]
    public void Normalizers_KeepPackageTimingInOperationalRange()
    {
        Assert.Equal(15, TopOfHourScheduler.NormalizeCadence(1));
        Assert.Equal(1440, TopOfHourScheduler.NormalizeCadence(2000));
        Assert.Equal(0.25, TopOfHourScheduler.NormalizeFadeOutSeconds(-1), precision: 3);
        Assert.Equal(10, TopOfHourScheduler.NormalizeFadeOutSeconds(99), precision: 3);
        Assert.Equal(60, TopOfHourScheduler.NormalizeIntroGraceSeconds(99));
        Assert.Equal(60, TopOfHourScheduler.NormalizeLateWindowSeconds(1));
        Assert.Equal(900, TopOfHourScheduler.NormalizeLateWindowSeconds(9999));
        Assert.Equal(300, TopOfHourScheduler.DefaultLateWindowSeconds);
    }
}
