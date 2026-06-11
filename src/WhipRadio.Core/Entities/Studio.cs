namespace WhipRadio.Core.Entities;

public enum StudioKind
{
    /// <summary>Music AI endpoint (ACE-Step, MusicGen, online APIs…).</summary>
    Recording,

    /// <summary>TTS endpoint producing voice announcements.</summary>
    VoiceBooth,
}

/// <summary>
/// A connected production endpoint: a recording studio (music AI) or a voice
/// booth (TTS). Studios live OUTSIDE the app's lifecycle — local containers or
/// online APIs — and artists/hosts queue for the first free one.
/// </summary>
public class Studio
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public StudioKind Kind { get; set; }

    /// <summary>Base URL of the container or API.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Protocol the endpoint speaks: detected by the test probe for
    /// local containers ("ace-step-1.5", "musicgen", "local-tts") or chosen
    /// explicitly for API studios ("elevenlabs").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>API key for online providers; null for local containers.</summary>
    public string? ApiKey { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public int JobsCompleted { get; set; }

    public int JobsFailed { get; set; }
}
