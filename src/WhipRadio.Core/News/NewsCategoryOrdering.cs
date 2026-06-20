using WhipRadio.Core.Entities;

namespace WhipRadio.Core.News;

public static class NewsCategoryOrdering
{
    public static readonly IReadOnlyList<string> DefaultOrder =
    [
        "general",
        "business",
        "technology",
        "sports",
        "culture",
        "regional",
    ];

    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return DefaultOrder;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = stored
            .Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeCategory)
            .Where(category => category.Length > 0)
            .Where(category => seen.Add(category))
            .ToList();

        foreach (var category in DefaultOrder)
        {
            if (seen.Add(category))
            {
                order.Add(category);
            }
        }

        return order.Count == 0 ? DefaultOrder : order;
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? categories)
        => Parse(categories is null ? null : string.Join(',', categories));

    public static string ToStorage(IEnumerable<string>? categories)
        => string.Join(',', Normalize(categories));

    public static IReadOnlyList<NewsFeed> SortFeeds(IEnumerable<NewsFeed> feeds, IEnumerable<string>? categoryOrder)
    {
        var rank = BuildRank(categoryOrder);
        return feeds
            .OrderBy(feed => Rank(rank, feed.Category))
            .ThenBy(feed => feed.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<NewsItem> SortItems(IEnumerable<NewsItem> items, IEnumerable<string>? categoryOrder)
    {
        var rank = BuildRank(categoryOrder);
        return items
            .OrderBy(item => Rank(rank, item.Feed?.Category))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, int> BuildRank(IEnumerable<string>? categoryOrder)
        => Normalize(categoryOrder)
            .Select((category, index) => new { category, index })
            .ToDictionary(item => item.category, item => item.index, StringComparer.OrdinalIgnoreCase);

    private static int Rank(IReadOnlyDictionary<string, int> rank, string? category)
        => rank.TryGetValue(NormalizeCategory(category), out var value) ? value : int.MaxValue;

    private static string NormalizeCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim().ToLowerInvariant();
}
