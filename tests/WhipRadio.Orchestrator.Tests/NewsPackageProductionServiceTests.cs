using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class NewsPackageProductionServiceTests
{
    [TestMethod]
    public void BuildIntroText_UsesCurrentLocalTimeAndVariesByHost()
    {
        var airtime = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);
        var currentHost = new Moderator { Id = 1, Name = "Ava" };
        var newsHost = new Moderator { Id = 2, Name = "Maya" };

        var intro = NewsPackageProductionService.BuildIntroText(currentHost, newsHost, airtime);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("It's 18:00. Maya has the news.", intro);
    }

    [TestMethod]
    public void BuildIntroText_UsesScheduledAirtimeAcrossMidnight()
    {
        var airtime = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.FromHours(2));
        var currentHost = new Moderator { Id = 1, Name = "Ava" };
        var newsHost = new Moderator { Id = 2, Name = "Maya" };

        var intro = NewsPackageProductionService.BuildIntroText(currentHost, newsHost, airtime);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("It's 00:00. Maya has the news.", intro);
    }

    [TestMethod]
    public void BuildIntroText_UsesShortSameHostVariant()
    {
        var localNow = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);
        var host = new Moderator { Id = 1, Name = "Ava" };

        var intro = NewsPackageProductionService.BuildIntroText(host, host, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("It's 18:00. Here is the news.", intro);
    }

    [TestMethod]
    public void BuildNewsFacts_PrependsBulletinTime()
    {
        var airtime = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);
        var item = new NewsItem
        {
            Title = "Markets move",
            Url = "https://example.com/story",
            PublishedAtUtc = new DateTime(2026, 6, 20, 16, 0, 0, DateTimeKind.Utc),
            Summary = "Stocks rose.",
            FirstSeenAtUtc = new DateTime(2026, 6, 20, 16, 5, 0, DateTimeKind.Utc),
            ContentHash = "abc123",
        };

        var facts = NewsPackageProductionService.BuildNewsFacts([item], airtime);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Contains("Bulletin time: 2026-06-20 18:00 local.", facts);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Contains("Title: Markets move", facts);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_WaitsForNewsBoundaryWhenNewsIsEnabled()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeNews);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeWeather);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_UsesWeatherWhenNewsIsDisabled()
    {
        var settings = new StationSettings
        {
            NewsEnabled = false,
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 21, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(plan.IncludeNews);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeWeather);
    }

    [TestMethod]
    public void ResolveNextPreparationPlan_WaitsForFullPackageWhenNewsIsEnabled()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 21, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPreparationPlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNull(plan);
    }

    [TestMethod]
    public void ResolveNextPreparationPlan_WaitsOutsidePrepareWindow()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 19, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPreparationPlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNull(plan);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_UsesNewsOnlyBlocksWhenWeatherIsDisabled()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = false,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeNews);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(plan.IncludeWeather);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_UsesNewsAndWeatherBlocksWhenTargetsMatch()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 45, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow);

        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeNews);
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(plan.IncludeWeather);
    }
}
