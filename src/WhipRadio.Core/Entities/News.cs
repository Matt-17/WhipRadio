namespace WhipRadio.Core.Entities;

public enum NewsItemStatus
{
    New,
    Selected,
    Rejected,
    Produced,
    Failed,
}

public enum NewsPackageStatus
{
    Pending,
    Retrying,
    Ready,
    Queued,
    Played,
    Failed,
}

public enum NewsPackageKind
{
    TopOfHour,
    LongFormat,
}

public class NewsFeed
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Language { get; set; } = "en";

    public string Region { get; set; } = "global";

    public string Category { get; set; } = "general";

    public bool IsEnabled { get; set; } = true;

    public bool IsSeeded { get; set; }

    public int PollCadenceMinutes { get; set; } = 30;

    public int MaxItemsPerPoll { get; set; } = 20;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastPolledAtUtc { get; set; }

    public string? LastError { get; set; }

    public List<NewsItem> Items { get; set; } = [];
}

public class NewsItem
{
    public Guid Id { get; set; }

    public Guid FeedId { get; set; }

    public NewsFeed? Feed { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? ExtractedSummary { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public NewsItemStatus Status { get; set; } = NewsItemStatus.New;

    public string? SelectionReason { get; set; }

    public DateTime? ProducedAtUtc { get; set; }
}

public class NewsPackage
{
    public Guid Id { get; set; }

    public NewsPackageKind Kind { get; set; } = NewsPackageKind.TopOfHour;

    public NewsPackageStatus Status { get; set; } = NewsPackageStatus.Pending;

    public DateTime TargetUtc { get; set; }

    public int TargetDurationSeconds { get; set; }

    public Guid? AnnouncementId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ProducedAtUtc { get; set; }

    public DateTime? QueuedAtUtc { get; set; }

    public DateTime? PlayedAtUtc { get; set; }

    public string? FailureReason { get; set; }

    public string? ProductionState { get; set; }

    public string? SourceSummary { get; set; }
}
