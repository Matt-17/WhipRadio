namespace WhipRadio.Core.Entities;

/// <summary>
/// A show format created by the program director (e.g. "Friday Party Night",
/// "Lofi Sundowner"). Formats own a host, a musical direction and a reason.
/// </summary>
public class Format
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Subgenre { get; set; } = string.Empty;

    public int? ModeratorId { get; set; }

    public Moderator? Moderator { get; set; }

    /// <summary>The program director's reasoning for this format.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Disabled formats are replaced by the director over time, not instantly.</summary>
    public bool IsEnabled { get; set; } = true;

    public int UpVotes { get; set; }

    public int DownVotes { get; set; }

    public DateTime CreatedAt { get; set; }
}
