namespace WhipRadio.Core.Entities;

public enum ArtistPostKind
{
    ArtistCreated,
    TrackReleased,
}

/// <summary>A text-only public update from a fictional artist or band.</summary>
public class ArtistPost
{
    public Guid Id { get; set; }

    public Guid ArtistId { get; set; }

    public Artist Artist { get; set; } = null!;

    public Guid? TrackId { get; set; }

    public Track? Track { get; set; }

    public ArtistPostKind Kind { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
