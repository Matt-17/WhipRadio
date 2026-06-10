namespace WhipRadio.Core.Entities;

/// <summary>A radio host persona. Drives announcement style, TTS voice and track selection.</summary>
public class Moderator
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>BCP-47 language tag, e.g. "de", "en".</summary>
    public string Language { get; set; } = "en";

    /// <summary>TTS voice key, e.g. "af_heart".</summary>
    public string VoiceId { get; set; } = "af_heart";

    /// <summary>TTS speed multiplier, 0.7–1.3.</summary>
    public double SpeechRate { get; set; } = 1.0;

    /// <summary>System prompt fragment for the VoiceDirector stage.</summary>
    public string PersonaPrompt { get; set; } = string.Empty;

    /// <summary>E.g. "fast-energetic", "slow-thoughtful".</summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>null = no preference; drives track selection.</summary>
    public bool? PrefersVocals { get; set; }

    /// <summary>CSV genre list, e.g. "rock,indie".</summary>
    public string PreferredGenres { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
