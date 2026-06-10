namespace WhipRadio.Core.Entities;

/// <summary>A listener's up/down vote on a track.</summary>
public class Vote
{
    public int Id { get; set; }

    public Guid TrackId { get; set; }

    public Track? Track { get; set; }

    /// <summary>+1 or -1.</summary>
    public int Direction { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Trivial client identifier, e.g. hashed IP.</summary>
    public string ClientHint { get; set; } = string.Empty;
}
