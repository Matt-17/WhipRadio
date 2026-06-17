namespace WhipRadio.Core.Entities;

public enum ModeratorMemoryLayer
{
    ImmutablePersona,
    FormatContext,
    DayMemory,
    LongTermMemory,
    CurrentContext,
}

/// <summary>
/// Layered host memory. DayMemory keeps what was said today; long-term and
/// persona layers can feed continuity without polluting short-lived recall.
/// </summary>
public class ModeratorMemory
{
    public int Id { get; set; }

    public int ModeratorId { get; set; }

    public ModeratorMemoryLayer Layer { get; set; } = ModeratorMemoryLayer.DayMemory;

    public DateOnly Date { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
