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

    /// <summary>
    /// Current high-level production step (1-based) for the in-progress package,
    /// surfaced as "k/N" in the Production page. 0 when not producing.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>Total high-level production steps for the in-progress package. 0 when not producing.</summary>
    public int StepTotal { get; set; }

    /// <summary>
    /// JSON-serialized list of <see cref="NewsPackageSegmentState"/> for segments already produced.
    /// Lets a restart/retry reuse finished segments (their written text + rendered audio) instead of
    /// re-producing the whole package, and lets a recreate expire the old segment audio precisely.
    /// </summary>
    public string? ProducedSegmentsJson { get; set; }
}

/// <summary>
/// A single produced top-of-hour segment, persisted on the package so production is resumable.
/// Holds the already-rendered announcement ids (intro/body/gap line), the voicing host, and the
/// news items it consumed, so a resumed run can re-attach them without re-writing or re-recording.
/// </summary>
public class NewsPackageSegmentState
{
    public string Key { get; set; } = string.Empty;

    public bool Done { get; set; }

    public Guid IntroAnnouncementId { get; set; }

    public Guid? BodyAnnouncementId { get; set; }

    public Guid? GapLineAnnouncementId { get; set; }

    /// <summary>Optional closing line that airs after the body (e.g. the news host returning
    /// after the weather forecast inside the weather segment).</summary>
    public Guid? OutroAnnouncementId { get; set; }

    public int SegmentHostModeratorId { get; set; }

    public string? DegradationReason { get; set; }

    public string SourceSummary { get; set; } = string.Empty;

    public List<Guid> SelectedItemIds { get; set; } = [];
}
