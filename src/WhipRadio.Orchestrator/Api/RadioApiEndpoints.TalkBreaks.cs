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
    private static void MapTalkBreaks(RouteGroupBuilder api)
    {
        api.MapPost("/talkbreaks/emergency", async (
            EmergencyTalkBreakRequestDto request,
            RadioDbContext db,
            ScheduleService schedule,
            AnnouncementFactory factory,
            PriorityTalkBreakDispatcher dispatcher,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest("Text is required.");
            }

            var priority = ParseOnDemandPriority(request.Priority);
            Moderator? moderator;
            if (request.ModeratorId is int moderatorId)
            {
                moderator = await db.Moderators.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == moderatorId && item.IsActive, ct);
                if (moderator is null)
                {
                    return Results.NotFound();
                }
            }
            else
            {
                moderator = (await schedule.GetCurrentAsync(ct)).Moderator;
            }

            var stationName = (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).StationName;
            var expiresInMinutes = Math.Clamp(request.ExpiresInMinutes ?? 60, 5, 24 * 60);
            var announcement = await factory.ProduceDirectAsync(
                AnnouncementKind.EmergencyMessage,
                TalkPartKind.EmergencyMessage,
                priority,
                moderator,
                request.Text,
                "EmergencyMessage",
                ct,
                expiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(expiresInMinutes));

            var talkBreakId = await db.TalkBreaks.AsNoTracking()
                .Where(talkBreak => talkBreak.AnnouncementId == announcement.Id)
                .Select(talkBreak => talkBreak.Id)
                .FirstAsync(ct);

            await dispatcher.PushReadyAsync(ct);

            return Results.Accepted(
                $"/api/announcements/{announcement.Id}/audio",
                new EmergencyTalkBreakDto(
                    announcement.Id,
                    talkBreakId,
                    priority.ToString(),
                    TalkBreakStatus.Rendered.ToString()));
        });
    }
}
