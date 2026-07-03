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
}
