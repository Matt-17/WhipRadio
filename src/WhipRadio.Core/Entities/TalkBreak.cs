namespace WhipRadio.Core.Entities;

public enum TalkBreakPriority
{
    Low,
    Normal,
    High,
    Emergency,
    Scheduled,
}

public enum TalkBreakStatus
{
    Pending,
    Rendered,
    Played,
    Expired,
    Failed,
}

public enum TalkPartKind
{
    PreviousSongComment,
    NextSongIntro,
    Weather,
    WeatherHandoff,
    ListenerGreeting,
    RequestDedication,
    Banter,
    PersonalNote,
    Joke,
    StationId,
    HostChange,
    TalkBit,
    Jingle,
    EmergencyMessage,
    News,
}

public enum TalkPartStatus
{
    Pending,
    Rendered,
    Played,
    Expired,
    Failed,
}

/// <summary>A spoken playout unit. Today it wraps one rendered announcement WAV; later it can render 1-N parts together.</summary>
public class TalkBreak
{
    public Guid Id { get; set; }

    public Guid? AnnouncementId { get; set; }

    public int ModeratorId { get; set; }

    public TalkBreakPriority Priority { get; set; } = TalkBreakPriority.Normal;

    public TalkBreakStatus Status { get; set; } = TalkBreakStatus.Pending;

    public string Purpose { get; set; } = string.Empty;

    public string Title { get; set; } = "Announcement";

    public DateTime? TargetWindowStartUtc { get; set; }

    public DateTime? TargetWindowEndUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RenderedAtUtc { get; set; }

    public DateTime? PlayedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public double DurationSeconds { get; set; }

    public List<TalkPart> Parts { get; set; } = [];
}

public class TalkPart
{
    public int Id { get; set; }

    public Guid TalkBreakId { get; set; }

    public TalkBreak? TalkBreak { get; set; }

    public int SortOrder { get; set; }

    public TalkPartKind Kind { get; set; }

    public TalkPartStatus Status { get; set; } = TalkPartStatus.Pending;

    public TalkBreakPriority Priority { get; set; } = TalkBreakPriority.Normal;

    public string Purpose { get; set; } = string.Empty;

    public Guid? AnnouncementId { get; set; }

    public Guid? RelatedTrackId { get; set; }

    public Guid? TalkBitId { get; set; }

    public Guid? JingleId { get; set; }

    public int? DesiredDurationSeconds { get; set; }

    public int? WordBudget { get; set; }

    public DateTime? TargetWindowStartUtc { get; set; }

    public DateTime? TargetWindowEndUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
