namespace WhipRadio.Core.Entities;

/// <summary>
/// A recurring podcast show: its own format block in the program grid with one
/// weekly slot. Each occurrence must be filled with a produced
/// <see cref="ConversationSegment"/> episode; when production misses the slot,
/// the block plays as normal music under the show's format (logged fallback).
/// </summary>
public class PodcastShow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The standing editorial brief; each episode invents a fresh angle within it.</summary>
    public string Brief { get; set; } = string.Empty;

    /// <summary>Spoken episode length (10–30 min); the rest of the slot is music.</summary>
    public int EpisodeMinutes { get; set; } = 20;

    /// <summary>0 = Sunday … 6 = Saturday (matches ProgramSlot.DayOfWeek).</summary>
    public int DayOfWeek { get; set; }

    /// <summary>Slot start, minutes after local midnight.</summary>
    public int StartMinute { get; set; }

    /// <summary>Grid slot length (≥30, the ProgramSlot minimum).</summary>
    public int SlotDurationMinutes { get; set; } = 30;

    /// <summary>JSON <c>List&lt;ConversationParticipant&gt;</c> — the fixed episode roster.</summary>
    public string ParticipantsJson { get; set; } = "[]";

    /// <summary>The seeded grid Format owned by this show.</summary>
    public Guid? FormatId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
}
