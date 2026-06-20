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
}
