namespace WhipRadio.Core.Entities;

/// <summary>
/// Short-term host memory: what a host talked about, kept per day so talks can
/// reference earlier banter instead of repeating themselves.
/// </summary>
public class ModeratorMemory
{
    public int Id { get; set; }

    public int ModeratorId { get; set; }

    public DateOnly Date { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
