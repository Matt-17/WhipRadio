using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Slugs;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private static void MapAgentLog(RouteGroupBuilder api)
    {
        api.MapGet("/agent-log", async (
            string? agent,
            int? take,
            AgentActionLogService service,
            CancellationToken ct) =>
            Results.Ok(await service.GetAsync(agent, take ?? 200, ct)));
    }

    private static void MapChat(RouteGroupBuilder api)
    {
        RouteGroupBuilder chat = api.MapGroup("/chat");

        chat.MapGet("/channels", async (ChatService service, CancellationToken ct) =>
            Results.Ok(await service.GetChannelsAsync(ct)));

        chat.MapGet("/channels/{id:guid}/messages", async (
            Guid id,
            DateTime? before,
            int? take,
            ChatService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.GetMessagesAsync(id, before, take ?? 50, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        chat.MapPost("/channels/{id:guid}/messages", async (
            Guid id,
            PostChatMessageRequest request,
            ChatService service,
            ChatResponderResolver responders,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest("Text is required.");
            }

            string text = request.Text.Trim();
            if (text.Length > 4000)
            {
                return Results.BadRequest("Text must be 4000 characters or fewer.");
            }

            try
            {
                Guid correlationId = Guid.NewGuid();
                ChatMessageDto posted = await service.PostAsync(
                    id,
                    ChatSenderKind.Admin,
                    moderatorId: null,
                    text,
                    actionsJson: null,
                    correlationId,
                    hopCount: 0,
                    ct);
                await responders.TryEnqueueForAdminMessageAsync(posted, ct);
                return Results.Ok(posted);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Everyone who can be invited into a group channel.
        chat.MapGet("/participants", async (RadioDbContext db, CancellationToken ct) =>
        {
            var hosts = await db.Moderators.AsNoTracking()
                .Where(host => host.IsActive)
                .OrderBy(host => host.Name)
                .Select(host => new ChatParticipantOptionDto(
                    nameof(ChatParticipantKind.Host), host.Id, null, host.Name, "host — " + host.Style))
                .ToListAsync(ct);
            var members = await db.ArtistMembers.AsNoTracking()
                .Include(member => member.Artist)
                .Where(member => member.Artist != null && !member.Artist.IsRetired)
                .OrderBy(member => member.Name)
                .Select(member => new ChatParticipantOptionDto(
                    nameof(ChatParticipantKind.ArtistMember), null, member.Id, member.Name,
                    member.Artist!.Name + " — " + member.Role))
                .ToListAsync(ct);
            var guests = await db.Guests.AsNoTracking()
                .Where(guest => !guest.IsArchived)
                .OrderBy(guest => guest.Name)
                .Select(guest => new ChatParticipantOptionDto(
                    nameof(ChatParticipantKind.Guest), null, guest.Id, guest.Name, "guest — " + guest.Expertise))
                .ToListAsync(ct);
            return Results.Ok(hosts.Concat(members).Concat(guests).ToList());
        });

        chat.MapPost("/channels/group", async (
            CreateGroupChannelRequestDto request,
            ChatService service,
            ChatParticipantResolver participants,
            CancellationToken ct) =>
        {
            if (request.Members is not { Count: > 0 })
            {
                return Results.BadRequest("A group channel needs at least one member.");
            }

            var resolved = new List<(WhipRadio.Core.Prompting.ChatParticipantRef Ref, string DisplayName)>();
            foreach (ChatParticipantSelectionDto selection in request.Members)
            {
                if (!TryToRef(selection, out var reference))
                {
                    return Results.BadRequest($"Invalid participant selection '{selection.Kind}'.");
                }

                var participant = await participants.ResolveAsync(reference, ct);
                if (participant is null)
                {
                    return Results.BadRequest($"Participant of kind '{selection.Kind}' was not found.");
                }

                if (resolved.All(entry => entry.Ref != participant.Ref))
                {
                    resolved.Add((participant.Ref, participant.DisplayName));
                }
            }

            return Results.Ok(await service.CreateGroupChannelAsync(request.Name, resolved, ct));
        });

        chat.MapPost("/channels/{id:guid}/members", async (
            Guid id,
            ChatParticipantSelectionDto selection,
            ChatService service,
            ChatParticipantResolver participants,
            CancellationToken ct) =>
        {
            if (!TryToRef(selection, out var reference))
            {
                return Results.BadRequest($"Invalid participant selection '{selection.Kind}'.");
            }

            var participant = await participants.ResolveAsync(reference, ct);
            if (participant is null)
            {
                return Results.NotFound("Participant was not found.");
            }

            try
            {
                bool added = await service.AddMemberAsync(id, participant.Ref, participant.DisplayName, ct);
                return added ? Results.Ok() : Results.Conflict("Already a member.");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        chat.MapDelete("/channels/{id:guid}/members/{memberId:guid}", async (
            Guid id,
            Guid memberId,
            ChatService service,
            CancellationToken ct) =>
            await service.RemoveMemberByIdAsync(id, memberId, ct) ? Results.NoContent() : Results.NotFound());

        chat.MapPost("/channels/{id:guid}/read", async (
            Guid id,
            ChatService service,
            CancellationToken ct) =>
        {
            try
            {
                await service.MarkReadAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        chat.MapPost("/actions/{messageId:guid}/{actionIndex:int}/confirm", (
            Guid messageId,
            int actionIndex) =>
            Results.Conflict(
                "Chat action confirmation is disabled in this phase because chat actions auto-run."));

        chat.MapPost("/actions/{messageId:guid}/{actionIndex:int}/dismiss", async (
            Guid messageId,
            int actionIndex,
            ChatService service,
            CancellationToken ct) =>
        {
            try
            {
                await service.DismissActionAsync(messageId, actionIndex, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }

    private static bool TryToRef(
        ChatParticipantSelectionDto selection,
        out WhipRadio.Core.Prompting.ChatParticipantRef reference)
    {
        reference = WhipRadio.Core.Prompting.ChatParticipantRef.Director;
        if (Enum.TryParse(selection.Kind, ignoreCase: true, out ChatParticipantKind kind))
        {
            switch (kind)
            {
                case ChatParticipantKind.Host when selection.ModeratorId is int moderatorId:
                    reference = WhipRadio.Core.Prompting.ChatParticipantRef.ForHost(moderatorId);
                    return true;
                case ChatParticipantKind.ArtistMember when selection.EntityId is Guid memberId:
                    reference = WhipRadio.Core.Prompting.ChatParticipantRef.ForArtistMember(memberId);
                    return true;
                case ChatParticipantKind.Guest when selection.EntityId is Guid guestId:
                    reference = WhipRadio.Core.Prompting.ChatParticipantRef.ForGuest(guestId);
                    return true;
            }
        }

        return false;
    }
}
