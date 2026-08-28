namespace WhipRadio.Web.Components.Library;

/// <summary>Vote formatting shared by the artist header and the track table.</summary>
internal static class LibraryFormat
{
    public static string VoteTone(int net) => net switch
    {
        > 0 => "positive",
        < 0 => "negative",
        _ => "neutral",
    };

    public static string FormatVoteNet(int net) => net > 0 ? $"+{net}" : net.ToString();
}

/// <summary>A queued/in-flight artist creation shown in the artist rail.</summary>
public sealed class PendingArtistCreation(string hint)
{
    public string Hint { get; } = hint;

    public PendingArtistCreationStatus Status { get; set; } = PendingArtistCreationStatus.Queued;

    public string? Error { get; set; }
}

/// <summary>A queued/in-flight artist redefinition shown next to the artist.</summary>
public sealed class PendingArtistRedefinition(Guid artistId)
{
    public Guid ArtistId { get; } = artistId;

    public PendingArtistRedefinitionStatus Status { get; set; } = PendingArtistRedefinitionStatus.Queued;

    public string? Error { get; set; }
}

public enum PendingArtistCreationStatus
{
    Queued,
    Creating,
    Failed,
}

public enum PendingArtistRedefinitionStatus
{
    Queued,
    Redefining,
    Failed,
}
