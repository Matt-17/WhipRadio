using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static class GreetingsApiEndpoints
{
    public static IEndpointRouteBuilder MapGreetingsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/greetings");

        api.MapPost("/", async (SubmitGreetingDto request, HttpContext http, RadioDbContext db,
            GreetingState state, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
            if (!settings.GreetingsEnabled)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.SenderName) || string.IsNullOrWhiteSpace(request.MessageText))
            {
                return Results.BadRequest("Name and message are required.");
            }

            var clientHint = HashClient(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (!state.TryRegisterSubmission(clientHint))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var pending = await db.ListenerMessages.CountAsync(
                m => m.Status == ListenerMessageStatus.Pending || m.Status == ListenerMessageStatus.Queued, ct);
            if (pending >= settings.MaxPendingGreetings)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var message = new ListenerMessage
            {
                Id = Guid.NewGuid(),
                SenderName = request.SenderName.Trim()[..Math.Min(30, request.SenderName.Trim().Length)],
                MessageText = request.MessageText.Trim()[..Math.Min(500, request.MessageText.Trim().Length)],
                Kind = request.Kind == "Request" ? ListenerMessageKind.Request : ListenerMessageKind.Greeting,
                RequestGenre = request.RequestGenre,
                RequestMood = request.RequestMood,
                SubmittedAt = DateTime.UtcNow,
                Status = ListenerMessageStatus.Pending,
            };

            // Saved as Pending; the MessageModerationService decides Queued vs.
            // Dismissed in the background so this request returns instantly.
            db.ListenerMessages.Add(message);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(message));
        });

        // Returns active messages (Pending/Queued/OnAir) plus recent dismissed history.
        api.MapGet("/", async (RadioDbContext db, CancellationToken ct) =>
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var messages = await db.ListenerMessages.AsNoTracking()
                .Where(m => m.Status == ListenerMessageStatus.Pending
                    || m.Status == ListenerMessageStatus.Queued
                    || m.Status == ListenerMessageStatus.OnAir
                    || (m.Status == ListenerMessageStatus.Dismissed && m.SubmittedAt >= cutoff))
                .OrderByDescending(m => m.SubmittedAt)
                .Take(100)
                .ToListAsync(ct);
            return Results.Ok(messages.Select(ToDto).ToList());
        });

        // Manual override endpoints so an admin can still intervene if needed.
        api.MapPost("/{id:guid}/queue", async (Guid id, RadioDbContext db, GreetingState state, CancellationToken ct) =>
        {
            var message = await db.ListenerMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (message is null)
            {
                return Results.NotFound();
            }

            message.Status = ListenerMessageStatus.Queued;
            message.DismissalReason = null;
            if (message.Kind == ListenerMessageKind.Request)
            {
                state.SetGenreHint(message.RequestGenre);
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(message));
        });

        api.MapPost("/{id:guid}/dismiss", async (Guid id, RadioDbContext db, CancellationToken ct) =>
        {
            var message = await db.ListenerMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (message is null)
            {
                return Results.NotFound();
            }

            message.Status = ListenerMessageStatus.Dismissed;
            message.DismissalReason = "Dismissed by host";
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(message));
        });

        return app;
    }

    private static ListenerMessageDto ToDto(ListenerMessage m) => new(
        m.Id, m.SenderName, m.MessageText, m.Kind.ToString(),
        m.RequestGenre, m.RequestMood, m.SubmittedAt, m.Status.ToString(),
        m.DismissalReason);

    private static string HashClient(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
