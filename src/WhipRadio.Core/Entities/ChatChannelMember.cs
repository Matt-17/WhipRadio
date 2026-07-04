namespace WhipRadio.Core.Entities;

/// <summary>Who can act as a chat participant besides the admin.</summary>
public enum ChatParticipantKind
{
    Host = 0,
    ArtistMember = 1,
    Guest = 2,
    Director = 3,
}

/// <summary>
/// Membership row for group channels (Phase 5). The classic channel kinds keep
/// their typed columns on <see cref="ChatChannel"/>; only Group channels use
/// this table. The admin is an implicit member of every channel.
/// </summary>
public class ChatChannelMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public ChatChannel? Channel { get; set; }

    public ChatParticipantKind Kind { get; set; }

    public int? ModeratorId { get; set; }

    public Moderator? Moderator { get; set; }

    public Guid? ArtistMemberId { get; set; }

    public ArtistMember? ArtistMember { get; set; }

    public Guid? GuestId { get; set; }

    public Guest? Guest { get; set; }

    /// <summary>Display-name snapshot taken when the member joined.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}
