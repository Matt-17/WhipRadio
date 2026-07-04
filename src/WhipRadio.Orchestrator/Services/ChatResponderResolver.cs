using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatResponderResolver(
    IDbContextFactory<RadioDbContext> dbFactory,
    ChatTurnQueue queue,
    ILogger<ChatResponderResolver> logger)
{
    public async Task<bool> TryEnqueueForAdminMessageAsync(ChatMessageDto message, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel? channel = await db.ChatChannels.AsNoTracking()
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == message.ChannelId, ct);
        if (channel is null)
        {
            return false;
        }

        ChatParticipantRef? responder = channel.Kind switch
        {
            ChatChannelKind.HostDm when channel.ModeratorId is int hostId => ChatParticipantRef.ForHost(hostId),
            ChatChannelKind.DirectorDm => null,
            ChatChannelKind.Station => await ResolveStationResponderAsync(db, message.Text, ct),
            ChatChannelKind.Group => ResolveGroupResponder(channel, message.Text, excludeSender: null, excludeName: null),
            _ => null,
        };

        bool isDirector = channel.Kind == ChatChannelKind.DirectorDm
            || (channel.Kind is ChatChannelKind.Station or ChatChannelKind.Group && MentionsDirector(message.Text));
        if (channel.Kind is ChatChannelKind.Station or ChatChannelKind.Group && responder is null && !isDirector)
        {
            logger.LogDebug("Chat message {MessageId} did not address a known responder", message.Id);
            return false;
        }

        bool queued = queue.TryEnqueue(new ChatTurnRequest(
            message.ChannelId,
            isDirector ? null : responder,
            message.Id,
            message.CorrelationId ?? Guid.NewGuid(),
            message.HopCount));
        return queued;
    }

    /// <summary>
    /// After an agent posts in a Group channel, other members it addressed by
    /// name get a turn — bounded by the hop cap so exchanges terminate.
    /// </summary>
    public async Task<bool> TryEnqueueForAgentMessageAsync(ChatMessageDto message, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel? channel = await db.ChatChannels.AsNoTracking()
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == message.ChannelId, ct);
        if (channel is null || channel.Kind != ChatChannelKind.Group)
        {
            return false;
        }

        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        if (message.HopCount + 1 > settings.ChatMaxAgentHops)
        {
            logger.LogDebug("Group chat exchange in {ChannelId} stopped at hop cap", channel.Id);
            return false;
        }

        ChatParticipantRef? responder = ResolveGroupResponder(channel, message.Text, ExcludeFromMessage(message), message.SenderName);
        if (responder is null && !MentionsDirector(message.Text))
        {
            return false;
        }

        return queue.TryEnqueue(new ChatTurnRequest(
            message.ChannelId,
            MentionsDirector(message.Text) && responder is null ? null : responder,
            message.Id,
            message.CorrelationId ?? Guid.NewGuid(),
            message.HopCount + 1));
    }

    private static ChatParticipantRef? ExcludeFromMessage(ChatMessageDto message)
        => message.SenderKind switch
        {
            nameof(ChatSenderKind.Host) when message.SenderModeratorId is int id => ChatParticipantRef.ForHost(id),
            _ => null,
        };

    private static ChatParticipantRef? ResolveGroupResponder(
        ChatChannel channel,
        string text,
        ChatParticipantRef? excludeSender,
        string? excludeName)
    {
        foreach (ChatChannelMember member in channel.Members)
        {
            if (string.IsNullOrWhiteSpace(member.DisplayName) || !Mentions(text, member.DisplayName))
            {
                continue;
            }

            if (excludeName is not null
                && member.DisplayName.Equals(excludeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ChatParticipantRef? reference = ToRef(member);
            if (reference is null || reference == excludeSender)
            {
                continue;
            }

            return reference;
        }

        return null;
    }

    private static ChatParticipantRef? ToRef(ChatChannelMember member)
        => member.Kind switch
        {
            ChatParticipantKind.Host when member.ModeratorId is int id => ChatParticipantRef.ForHost(id),
            ChatParticipantKind.ArtistMember when member.ArtistMemberId is Guid id => ChatParticipantRef.ForArtistMember(id),
            ChatParticipantKind.Guest when member.GuestId is Guid id => ChatParticipantRef.ForGuest(id),
            ChatParticipantKind.Director => ChatParticipantRef.Director,
            _ => null,
        };

    private static async Task<ChatParticipantRef?> ResolveStationResponderAsync(
        RadioDbContext db, string text, CancellationToken ct)
    {
        if (MentionsDirector(text))
        {
            return null;
        }

        List<Moderator> hosts = await db.Moderators.AsNoTracking()
            .Where(host => host.IsActive)
            .OrderBy(host => host.Name)
            .ToListAsync(ct);
        Moderator? match = hosts.FirstOrDefault(host => Mentions(text, host.Name));
        return match is null ? null : ChatParticipantRef.ForHost(match.Id);
    }

    private static bool MentionsDirector(string text)
        => Mentions(text, "Director") || Mentions(text, "Program Director");

    private static bool Mentions(string text, string name)
        => Regex.IsMatch(text, $@"(^|\b){Regex.Escape(name)}(\b|[:,])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
