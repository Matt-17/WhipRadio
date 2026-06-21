namespace WhipRadio.Core.Entities;

/// <summary>Member of a fictional artist or band.</summary>
public class ArtistMember
{
    public Guid Id { get; set; }

    public Guid ArtistId { get; set; }

    public Artist? Artist { get; set; }

    public int SortOrder { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Biography { get; set; } = string.Empty;

    public string VoiceCreationPrompt { get; set; } = string.Empty;

    public string TtsEngine { get; set; } = "qwen";

    public string? VoiceId { get; set; }

    /// <summary>Relative to the /data root.</summary>
    public string? VoiceReferencePath { get; set; }

    public DateTime? VoiceDesignedAtUtc { get; set; }

    public string? VoiceDesignLastError { get; set; }
}
