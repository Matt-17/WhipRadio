using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class NewsLongFormatSegmentContributorTests
{
    private static readonly string[] DefaultOrder =
        ["general", "business", "technology", "sports", "culture", "regional"];

    private static NewsLongFormatSegmentContributor Contributor()
        => new(null!, null!, null!, NullLogger<NewsLongFormatSegmentContributor>.Instance);

    private static NewsItem Item(string category, string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Url = $"https://example.com/{Guid.NewGuid():N}",
        PublishedAtUtc = DateTime.UtcNow,
        Feed = new NewsFeed { Category = category, Label = $"{category} feed" },
    };

    [TestMethod]
    public void ChapterBudget_ScalesWithDurationAndClamps()
    {
        Assert.Equal(6, NewsLongFormatSegmentContributor.ChapterBudget(30)); // (30-3)/4 = 6
        Assert.Equal(7, NewsLongFormatSegmentContributor.ChapterBudget(60)); // clamped at 7
        Assert.Equal(6, NewsLongFormatSegmentContributor.ChapterBudget(5));  // duration normalizes to 30
    }

    [TestMethod]
    public void BuildChapters_GroupsByCategoryInStationOrder()
    {
        var items = new[]
        {
            Item("technology", "t1"),
            Item("general", "g1"),
            Item("technology", "t2"),
            Item("business", "b1"),
        };

        var chapters = NewsLongFormatSegmentContributor.BuildChapters(items, DefaultOrder, 30);

        Assert.Equal(new[] { "general", "business", "technology" }, chapters.Select(c => c.Category).ToArray());
        Assert.Equal(2, chapters.Single(c => c.Category == "technology").Items.Count);
    }

    [TestMethod]
    public void BuildChapters_FoldsSurplusTopicsIntoTheLastChapter()
    {
        // 8 distinct categories but a 30-min show only budgets 6 chapters:
        // the surplus categories fold into the last chapter instead of being dropped.
        var categories = new[] { "general", "business", "technology", "sports", "culture", "regional", "science", "health" };
        var items = categories.Select(category => Item(category, $"{category} story")).ToList();

        var chapters = NewsLongFormatSegmentContributor.BuildChapters(items, DefaultOrder, 30);

        Assert.Equal(6, chapters.Count);
        Assert.Equal(3, chapters[^1].Items.Count); // regional + science + health
        Assert.Equal(items.Count, chapters.Sum(c => c.Items.Count)); // no story lost
    }

    [TestMethod]
    public void IsIncludedAt_MatchesConfiguredAirTimesOnly()
    {
        var contributor = Contributor();
        var settings = new StationSettings
        {
            NewsEnabled = true,
            NewsLongFormatEnabled = true,
            NewsLongFormatAirTimes = "08:00,20:00",
        };

        Assert.True(contributor.IsIncludedAt(
            settings, new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.FromHours(2))));
        Assert.False(contributor.IsIncludedAt(
            settings, new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.FromHours(2))));

        settings.NewsLongFormatEnabled = false;
        Assert.False(contributor.IsIncludedAt(
            settings, new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.FromHours(2))));
    }

    [TestMethod]
    public void NextOwnTarget_ReturnsUpcomingAirTime()
    {
        var contributor = Contributor();
        var settings = new StationSettings
        {
            NewsEnabled = true,
            NewsLongFormatEnabled = true,
            NewsLongFormatAirTimes = "08:00,20:00",
        };
        var localNow = new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 4, 20, 0, 0, TimeSpan.FromHours(2)),
            contributor.NextOwnTarget(settings, localNow)!.Value);
    }
}
