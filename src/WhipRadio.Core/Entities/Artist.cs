namespace WhipRadio.Core.Entities;

/// <summary>
/// A fictional artist persona. Tracks belong to an artist; the artist's style
/// descriptor drives generation, and listener votes on their tracks decide
/// whether an artist keeps producing or slowly dies out.
/// </summary>
public class Artist
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Subgenre { get; set; } = string.Empty;

    /// <summary>Prompt fragment describing the artist's signature sound.</summary>
    public string StyleDescriptor { get; set; } = string.Empty;

    /// <summary>Band, solo act, duo, collective, etc.</summary>
    public string Type { get; set; } = "Artist";

    /// <summary>Where this artist is from in the station's fictional world.</summary>
    public string? Origin { get; set; }

    public int? FormationYear { get; set; }

    /// <summary>One-line prompt that caused the artist to be discovered.</summary>
    public string? CreationHint { get; set; }

    /// <summary>Public short biography shown in the library.</summary>
    public string? Biography { get; set; }

    /// <summary>Hidden long-form background used for song and talk generation.</summary>
    public string? DeepBackgroundBiography { get; set; }

    /// <summary>Public showcase copy for the artist page.</summary>
    public string? PromotionText { get; set; }

    /// <summary>Original rich artist-generation prompt for reproducibility.</summary>
    public string? GenerationPrompt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Disliked artists stop getting new tracks and rotate out.</summary>
    public bool IsRetired { get; set; }

    public ICollection<ArtistMember> Members { get; set; } = [];
}
