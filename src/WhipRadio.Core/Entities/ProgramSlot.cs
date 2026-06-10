namespace WhipRadio.Core.Entities;

/// <summary>
/// One block in the weekly program (30 min – 4 h). FormatId == null means the
/// program director has not planned this spot yet ("still in planning").
/// </summary>
public class ProgramSlot
{
    public int Id { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (System.DayOfWeek values).</summary>
    public int DayOfWeek { get; set; }

    /// <summary>Minutes after midnight, station local time.</summary>
    public int StartMinute { get; set; }

    /// <summary>30–240 minutes.</summary>
    public int DurationMinutes { get; set; }

    public Guid? FormatId { get; set; }

    public Format? Format { get; set; }
}
