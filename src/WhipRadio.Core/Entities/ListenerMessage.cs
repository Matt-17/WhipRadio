namespace WhipRadio.Core.Entities;

public enum ListenerMessageKind
{
    Greeting,
    Request,
}

public enum ListenerMessageStatus
{
    Pending,
    Queued,
    OnAir,
    Dismissed,
}

/// <summary>A greeting or music request sent by a listener via the web app.</summary>
public class ListenerMessage
{
    public Guid Id { get; set; }

    public string SenderName { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public ListenerMessageKind Kind { get; set; }

    public string? RequestGenre { get; set; }

    public string? RequestMood { get; set; }

    public DateTime SubmittedAt { get; set; }

    public ListenerMessageStatus Status { get; set; }

    public int? ModeratorId { get; set; }

    public Guid? AnnouncementId { get; set; }
}
