using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.News;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class NewsFeedPollingService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<NewsFeedPollingService> logger)
{
    public async Task PollEnabledFeedsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var feeds = await db.NewsFeeds
            .Where(feed => feed.IsEnabled)
            .OrderBy(feed => feed.LastPolledAtUtc ?? DateTime.MinValue)
            .ThenBy(feed => feed.Category)
            .ThenBy(feed => feed.Label)
            .ToListAsync(ct);

        if (feeds.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<INewsFeedReader>();

        foreach (var feed in feeds)
        {
            try
            {
                var entries = await reader.ReadAsync(feed, Math.Clamp(feed.MaxItemsPerPoll, 1, 100), ct);
                var inserted = 0;
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Url))
                    {
                        continue;
                    }

                    var url = entry.Url.Trim();
                    if (await db.NewsItems.AnyAsync(item => item.FeedId == feed.Id && item.Url == url, ct))
                    {
                        continue;
                    }

                    db.NewsItems.Add(new NewsItem
                    {
                        Id = Guid.NewGuid(),
                        FeedId = feed.Id,
                        Title = TrimRequired(entry.Title, 300),
                        Url = TrimRequired(url, 1000),
                        Summary = Trim(entry.Summary, 1200),
                        PublishedAtUtc = entry.PublishedAtUtc,
                        FirstSeenAtUtc = now,
                        ContentHash = RssNewsFeedReader.ContentHash(entry),
                    });
                    inserted++;
                }

                feed.LastPolledAtUtc = now;
                feed.LastError = null;
                await db.SaveChangesAsync(ct);
                if (inserted > 0)
                {
                    logger.LogInformation("News feed {Feed} produced {Count} new item(s)", feed.Label, inserted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                feed.LastPolledAtUtc = now;
                feed.LastError = Trim(ex.GetBaseException().Message, 500);
                await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "News feed {Feed} failed", feed.Label);
            }
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string TrimRequired(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
