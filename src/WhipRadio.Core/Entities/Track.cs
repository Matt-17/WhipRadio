namespace WhipRadio.Core.Entities;

/// <summary>A generated song in the station's record collection.</summary>
public class Track
{
    public Guid Id { get; set; }

    /// <summary>LLM-invented title.</summary>
    public string Title { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    /// <summary>Free-form prompt descriptor used at generation time.</summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>musicgen ⇒ false, ace-step ⇒ true.</summary>
    public bool HasVocals { get; set; }

    /// <summary>Only for vocal tracks.</summary>
    public string? Lyrics { get; set; }

    /// <summary>Probed via ffprobe after generation.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Relative to the /data root.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Full prompt sent to the music backend.</summary>
    public string GenerationPrompt { get; set; } = string.Empty;

    /// <summary>"musicgen" | "ace-step".</summary>
    public string Backend { get; set; } = "musicgen";

    public DateTime CreatedAt { get; set; }

    public int PlayCount { get; set; }

    public int UpVotes { get; set; }

    public int DownVotes { get; set; }

    /// <summary>Heavily disliked tracks stop rotating.</summary>
    public bool IsRetired { get; set; }
}
