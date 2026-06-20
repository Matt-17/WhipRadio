using WhipRadio.Core.Entities;
using WhipRadio.Core.News;

namespace WhipRadio.Core.Tests;

[TestClass]
public class NewsCategoryOrderingTests
{
    [TestMethod]
    public void SortItems_OrdersByConfiguredCategoryThenTitle()
    {
        var items = new[]
        {
            Item("regional", "Zoo closure"),
            Item("business", "Zinc market"),
            Item("business", "Apple earnings"),
            Item("general", "Budget vote"),
        };

        var sorted = NewsCategoryOrdering.SortItems(items, ["business", "general", "regional"]);

        string[] expected = ["Apple earnings", "Zinc market", "Budget vote", "Zoo closure"];
        Assert.Equal(expected, sorted.Select(item => item.Title).ToArray());
    }

    [TestMethod]
    public void Parse_AddsDefaultCategoriesAfterCustomOrder()
    {
        var parsed = NewsCategoryOrdering.Parse("technology,general");

        Assert.Equal("technology", parsed[0]);
        Assert.Equal("general", parsed[1]);
        Assert.Contains("business", parsed);
        Assert.Contains("culture", parsed);
        Assert.Contains("regional", parsed);
    }

    private static NewsItem Item(string category, string title)
        => new()
        {
            Title = title,
            Feed = new NewsFeed { Category = category, Label = category },
        };
}
