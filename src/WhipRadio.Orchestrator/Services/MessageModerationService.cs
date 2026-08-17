using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Auto-moderates pending listener messages: the LLM decides whether a greeting
/// is safe to read on air and whether a music request fits the station, so no
/// human approval is needed. Approved requests leave their genre hint for the
/// next production cycle; rejected messages keep the reason for the admin view.
/// </summary>
public class MessageModerationService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    GreetingState greetingState,
    IHubContext<RadioHub> hub,
    ILogger<MessageModerationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ModeratePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message moderation cycle failed ({Reason})", ex.GetBaseException().Message);
            }

            await stoppingToken.DelayNoThrow(PollInterval);
        }
    }

    private async Task ModeratePendingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.ListenerMessages
            .Where(m => m.Status == ListenerMessageStatus.Pending)
            .OrderBy(m => m.SubmittedAt)
            .Take(5)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var context = await schedule.GetCurrentAsync(ct);

        using var scope = scopeFactory.CreateScope();
        var moderator = scope.ServiceProvider.GetRequiredService<MessageModerator>();

        foreach (var message in pending)
        {
            var result = await moderator.ModerateAsync(message, context, settings.StationName, ct);

            if (result.Approved)
            {
                message.Status = ListenerMessageStatus.Queued;
                if (message.Kind == ListenerMessageKind.Request)
                {
                    // The listener's explicit genre wins; otherwise the LLM's read of the message.
                    if (string.IsNullOrWhiteSpace(message.RequestGenre))
                    {
                        message.RequestGenre = result.ExtractedGenre;
                    }

                    greetingState.EnqueueRequestHint(message.Id, message.RequestGenre);
                }

                logger.LogInformation(
                    "Approved {Kind} from {Sender}{Genre}", message.Kind, message.SenderName,
                    message.RequestGenre is null ? "" : $" (genre: {message.RequestGenre})");
            }
            else
            {
                message.Status = ListenerMessageStatus.Dismissed;
                message.DismissalReason = result.Reason ?? "Rejected by auto-moderation";
                logger.LogInformation(
                    "Dismissed {Kind} from {Sender}: {Reason}", message.Kind, message.SenderName, message.DismissalReason);
            }

            await db.SaveChangesAsync(ct);
        }

        // One push after the batch so the Messages page reloads its current view.
        await hub.Clients.All.SendAsync("ListenerMessagesChanged", ct);
    }
}
