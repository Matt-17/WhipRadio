namespace WhipRadio.Core.Entities;

public enum AnnouncementKind
{
    SongIntro,
    SongOutro,
    Weather,
    Joke,
    Banter,
    StationId,

    /// <summary>Host hands over / takes over the show (hello & goodbye).</summary>
    HostChange,

    /// <summary>Personal anecdote referencing the host's day memory.</summary>
    PersonalNote,
}

/// <summary>A produced spoken segment (two-stage LLM pipeline + TTS).</summary>
public class Announcement
{
    public Guid Id { get; set; }

    public int ModeratorId { get; set; }

    public Moderator? Moderator { get; set; }

    public AnnouncementKind Kind { get; set; }

    /// <summary>Stage-1 (ScriptWriter) output.</summary>
    public string ScriptText { get; set; } = string.Empty;

    /// <summary>Stage-2 (VoiceDirector) output with speech markers.</summary>
    public string VoicedText { get; set; } = string.Empty;

    /// <summary>Relative to the /data root.</summary>
    public string FilePath { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public Guid? RelatedTrackId { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool WasPlayed { get; set; }
}
