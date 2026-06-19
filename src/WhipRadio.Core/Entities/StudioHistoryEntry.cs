namespace WhipRadio.Core.Entities;

/// <summary>Append-only operator trace for prompts sent to writer rooms,
/// recording studios, and voice booths.</summary>
public class StudioHistoryEntry
{
    public Guid Id { get; set; }

    public Guid? StudioId { get; set; }

    public Studio? Studio { get; set; }

    public string StudioName { get; set; } = string.Empty;

    public StudioKind StudioKind { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string Status { get; set; } = StudioHistoryStatus.Running;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? Result { get; set; }

    public string? Detail { get; set; }

    public string? Error { get; set; }
}

public static class StudioHistoryStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
