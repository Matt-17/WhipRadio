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

    public DateTime CreatedAt { get; set; }

    /// <summary>Disliked artists stop getting new tracks and rotate out.</summary>
    public bool IsRetired { get; set; }
}
