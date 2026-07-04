using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Prompting;

/// <summary>
/// Identity of a chat participant: a host, an artist band member, a one-off
/// guest, or the Program Director. Serializable-by-value so it can travel in
/// <c>ChatTurnRequest</c>s.
/// </summary>
public sealed record ChatParticipantRef(
    ChatParticipantKind Kind,
    int? ModeratorId = null,
    Guid? EntityId = null)
{
    public static readonly ChatParticipantRef Director = new(ChatParticipantKind.Director);

    public static ChatParticipantRef ForHost(int moderatorId)
        => new(ChatParticipantKind.Host, moderatorId);

    public static ChatParticipantRef ForArtistMember(Guid artistMemberId)
        => new(ChatParticipantKind.ArtistMember, EntityId: artistMemberId);

    public static ChatParticipantRef ForGuest(Guid guestId)
        => new(ChatParticipantKind.Guest, EntityId: guestId);
}

/// <summary>
/// A resolved chat participant with everything a turn needs: display name, the
/// permission role for the tool catalogue, and the persona text for the prompt.
/// <see cref="Moderator"/> is set only for hosts (the Moderator-specific prompt
/// blocks — traits, talk profile — apply to hosts alone).
/// </summary>
public sealed record ChatParticipant(
    ChatParticipantRef Ref,
    string DisplayName,
    CharacterRole Role,
    string PersonaSummary,
    Moderator? Moderator)
{
    public ChatParticipantKind Kind => Ref.Kind;

    public ChatSenderKind SenderKind => Ref.Kind switch
    {
        ChatParticipantKind.Host => ChatSenderKind.Host,
        ChatParticipantKind.ArtistMember => ChatSenderKind.ArtistMember,
        ChatParticipantKind.Guest => ChatSenderKind.Guest,
        _ => ChatSenderKind.Director,
    };
}
