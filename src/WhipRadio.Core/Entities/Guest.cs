namespace WhipRadio.Core.Entities;

/// <summary>
/// One-off invented guest (expert, character, caller) who can join chat and
/// on-air conversations. Not station staff and not an artist member — guests
/// carry their own persona and designed speaking voice.
/// </summary>
public class Guest
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    /// <summary>What the guest is known for ("urban beekeeper", "sleep researcher").</summary>
    public string Expertise { get; set; } = string.Empty;

    /// <summary>"male"/"female"; empty = unknown.</summary>
    public string Gender { get; set; } = string.Empty;

    public int? Age { get; set; }

    /// <summary>Comma-separated interests concrete enough to argue about in a podcast.</summary>
    public string Interests { get; set; } = string.Empty;

    /// <summary>One-line personality descriptor.</summary>
    public string Personality { get; set; } = string.Empty;

    /// <summary>Public short bio shown in the console.</summary>
    public string Biography { get; set; } = string.Empty;

    /// <summary>Hidden long-form background used only in generation prompts.</summary>
    public string DeepBackground { get; set; } = string.Empty;

    public string? CreationHint { get; set; }

    /// <summary>Full rendered creation prompt, stored for reproducibility.</summary>
    public string? GenerationPrompt { get; set; }

    public string TtsEngine { get; set; } = "qwen";

    public string? VoiceId { get; set; }

    /// <summary>Speaking timbre description the voice is designed from.</summary>
    public string VoiceCreationPrompt { get; set; } = string.Empty;

    /// <summary>Relative to the /data root.</summary>
    public string? VoiceReferencePath { get; set; }

    public DateTime? VoiceDesignedAtUtc { get; set; }

    public string? VoiceDesignLastError { get; set; }

    /// <summary>
    /// Optional post-TTS effect chain applied to this guest's on-air audio
    /// ("telephone" = band-limited caller sound); null = clean voice.
    /// </summary>
    public string? VoiceFx { get; set; }

    /// <summary>Archived guests are kept for history but no longer offered for invites.</summary>
    public bool IsArchived { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
