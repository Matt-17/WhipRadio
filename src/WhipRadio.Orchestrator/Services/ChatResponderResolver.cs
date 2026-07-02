using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
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
            .FirstOrDefaultAsync(item => item.Id == message.ChannelId, ct);
        if (channel is null)
        {
            return false;
        }

        int? responderId = channel.Kind switch
        {
            ChatChannelKind.HostDm => channel.ModeratorId,
            ChatChannelKind.DirectorDm => null,
            ChatChannelKind.Station => await ResolveStationResponderAsync(db, message.Text, ct),
            _ => null,
        };

        bool isDirector = channel.Kind == ChatChannelKind.DirectorDm
            || (channel.Kind == ChatChannelKind.Station && MentionsDirector(message.Text));
        if (channel.Kind == ChatChannelKind.Station && responderId is null && !isDirector)
        {
            logger.LogDebug("Station chat message {MessageId} did not address a known responder", message.Id);
            return false;
        }

        bool queued = queue.TryEnqueue(new ChatTurnRequest(
            message.ChannelId,
            isDirector ? null : responderId,
            message.Id,
            message.CorrelationId ?? Guid.NewGuid(),
            message.HopCount));
        return queued;
    }

    private static async Task<int?> ResolveStationResponderAsync(RadioDbContext db, string text, CancellationToken ct)
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
        return match?.Id;
    }

    private static bool MentionsDirector(string text)
        => Mentions(text, "Director") || Mentions(text, "Program Director");

    private static bool Mentions(string text, string name)
        => Regex.IsMatch(text, $@"(^|\b){Regex.Escape(name)}(\b|[:,])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
