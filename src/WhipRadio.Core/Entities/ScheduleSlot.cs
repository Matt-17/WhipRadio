namespace WhipRadio.Core.Entities;

/// <summary>Coarse genre-per-hour schedule (Phase 1).</summary>
public class ScheduleSlot
{
    public int Id { get; set; }

    /// <summary>0–23, station local time.</summary>
    public int HourOfDay { get; set; }

    public string Genre { get; set; } = string.Empty;

    public int ModeratorId { get; set; }

    public Moderator? Moderator { get; set; }
}
