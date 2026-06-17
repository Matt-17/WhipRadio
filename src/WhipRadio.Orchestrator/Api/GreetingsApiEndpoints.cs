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
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
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

        // Full message history, newest first — paged, optionally filtered by kind.
        // AiredAt comes from the play log of the linked announcement.
        api.MapGet("/", async (RadioDbContext db, int page, int pageSize, string? kind, CancellationToken ct) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize == 0 ? 25 : pageSize, 1, 100);

            var query = db.ListenerMessages.AsNoTracking();
            if (Enum.TryParse<ListenerMessageKind>(kind, ignoreCase: true, out var parsedKind))
            {
                query = query.Where(m => m.Kind == parsedKind);
            }

            var total = await query.CountAsync(ct);
            var messages = await query
                .OrderByDescending(m => m.SubmittedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var announcementIds = messages
                .Where(m => m.AnnouncementId != null)
                .Select(m => m.AnnouncementId!.Value)
                .ToList();
            var airTimes = await db.PlayLog.AsNoTracking()
                .Where(e => e.ItemType == PlayoutItemType.Announcement && announcementIds.Contains(e.ItemId))
                .GroupBy(e => e.ItemId)
                .Select(g => new { g.Key, PlayedAt = g.Min(e => e.PlayedAt) })
                .ToDictionaryAsync(x => x.Key, x => x.PlayedAt, ct);

            var items = messages.Select(m => ToDto(m) with
            {
                AiredAt = m.AnnouncementId is { } annId && airTimes.TryGetValue(annId, out var played)
                    ? played
                    : null,
            }).ToList();

            return Results.Ok(new PagedListenerMessagesDto(total, items));
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
                state.EnqueueRequestHint(message.Id, message.RequestGenre);
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
