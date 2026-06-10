namespace WhipRadio.Core.Entities;

public enum PlayoutItemType
{
    Track,
    Announcement,
}

/// <summary>One row per item that went on air.</summary>
public class PlayLogEntry
{
    public int Id { get; set; }

    public DateTime PlayedAt { get; set; }

    public PlayoutItemType ItemType { get; set; }

    public Guid ItemId { get; set; }

    public int? ModeratorId { get; set; }

    public double DurationSeconds { get; set; }
}
