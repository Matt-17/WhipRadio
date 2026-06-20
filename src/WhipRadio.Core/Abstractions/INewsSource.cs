using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

public sealed record NewsFeedEntry(
    string Title,
    string Url,
    string? Summary,
    DateTime? PublishedAtUtc);

public interface INewsFeedReader
{
    Task<IReadOnlyList<NewsFeedEntry>> ReadAsync(
        NewsFeed feed,
        int maxItems,
        CancellationToken ct);
}

public interface INewsArticleExtractor
{
    Task<string?> ExtractAsync(string url, CancellationToken ct);
}
