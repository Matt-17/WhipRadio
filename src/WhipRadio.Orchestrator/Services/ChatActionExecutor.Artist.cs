using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecutePostArtistFeedAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Guid memberId = context.Sender.Ref.EntityId
            ?? throw new InvalidOperationException("The artist sender has no member identity.");
        string body = Require(call, "body");

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Artist artist = await db.ArtistMembers.AsNoTracking()
            .Where(member => member.Id == memberId)
            .Select(member => member.Artist!)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Your band no longer exists in the library.");

        Guid? trackId = null;
        string? trackArg = Optional(call, "track");
        if (!string.IsNullOrWhiteSpace(trackArg))
        {
            Track? track = null;
            if (Guid.TryParse(trackArg, out Guid parsedId))
            {
                track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parsedId, ct);
            }

            track ??= await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ArtistId == artist.Id && t.Title.ToLower() == trackArg.Trim().ToLower(), ct);
            if (track is null)
            {
                return Failed(call, $"Track '{trackArg}' was not found.");
            }

            if (track.ArtistId != artist.Id)
            {
                return Failed(call, "You can only link your own tracks.");
            }

            trackId = track.Id;
        }

        await socialFeed.CreateAgentPostAsync(artist.Id, body, trackId, ct);
        return Succeeded(call, "Your post is live on the artist feed.");
    }

    private async Task<ChatActionRecord> ExecuteRequestSongFromArtistAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string brief = Require(call, "brief");
        Guid memberId = await ResolveArtistMemberIdAsync(Require(call, "artist"), ct);

        Guid channelId = await ResolveSharedGroupChannelAsync(context, memberId, ct);
        ChatMessageDto posted = await chat.PostAsync(
            channelId,
            context.SenderKind,
            context.SenderModerator?.Id,
            brief,
            null,
            context.CorrelationId,
            context.HopCount + 1,
            ct);
        await TryEnqueueAsync(channelId, ChatParticipantRef.ForArtistMember(memberId), posted.Id, context, call);
        return Succeeded(call, "Song request sent to the artist; they'll decide whether to record it.");
    }

    /// <summary>Resolves a member name, or a band name to its first voiced member.</summary>
    private async Task<Guid> ResolveArtistMemberIdAsync(string name, CancellationToken ct)
    {
        ChatParticipant? person = await participants.ResolveByNameAsync(name, ct);
        if (person is { Kind: ChatParticipantKind.ArtistMember } && person.Ref.EntityId is { } id)
        {
            return id;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        string lowered = name.Trim().ToLowerInvariant();
        Artist? band = await db.Artists.AsNoTracking()
            .Include(artist => artist.Members)
            .FirstOrDefaultAsync(artist => !artist.IsRetired && artist.Name.ToLower() == lowered, ct);
        ArtistMember? voiced = band?.Members
            .Where(member => !string.IsNullOrWhiteSpace(member.VoiceId))
            .OrderBy(member => member.SortOrder)
            .FirstOrDefault();
        return voiced?.Id
            ?? throw new InvalidOperationException(
                $"No artist or band member named '{name}' with a designed voice was found.");
    }

    /// <summary>
    /// The current channel when it is a group holding the member, otherwise any
    /// shared group channel. Artists have no DMs, so a group is the only reach.
    /// </summary>
    private async Task<Guid> ResolveSharedGroupChannelAsync(
        ChatActionContext context,
        Guid memberId,
        CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (context.Channel.Kind == ChatChannelKind.Group)
        {
            bool here = await db.ChatChannelMembers.AsNoTracking()
                .AnyAsync(member => member.ChannelId == context.Channel.Id && member.ArtistMemberId == memberId, ct);
            if (here)
            {
                return context.Channel.Id;
            }
        }

        Guid channelId = await db.ChatChannelMembers.AsNoTracking()
            .Where(member => member.ArtistMemberId == memberId
                && member.Channel!.Kind == ChatChannelKind.Group
                && !member.Channel.IsArchived)
            .Select(member => member.ChannelId)
            .FirstOrDefaultAsync(ct);
        return channelId != Guid.Empty
            ? channelId
            : throw new InvalidOperationException(
                "You share no group channel with that artist. The Program Director can Invite them into one.");
    }
}
