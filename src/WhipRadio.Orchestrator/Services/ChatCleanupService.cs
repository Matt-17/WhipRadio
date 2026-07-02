using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatCleanupService(
    IDbContextFactory<RadioDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ChatCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CycleDelay = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat cleanup failed");
            }

            try
            {
                await Task.Delay(CycleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task CleanupAsync(CancellationToken ct)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime recentFloor = now.AddDays(-7);
        DateTime pendingCutoff = now.AddHours(-24);
        DateTime hostToHostCutoff = now.AddDays(-30);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        int retention = Math.Max(1, settings.ChatRetainedMessagesPerChannel);
        List<Guid> channelIds = await db.ChatChannels.AsNoTracking()
            .Select(channel => channel.Id)
            .ToListAsync(ct);

        int deleted = 0;
        foreach (Guid channelId in channelIds)
        {
            List<Guid> retainedIds = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ChannelId == channelId)
                .OrderByDescending(message => message.CreatedAtUtc)
                .Take(retention)
                .Select(message => message.Id)
                .ToListAsync(ct);

            deleted += await db.ChatMessages
                .Where(message => message.ChannelId == channelId
                    && message.CreatedAtUtc < recentFloor
                    && !retainedIds.Contains(message.Id))
                .ExecuteDeleteAsync(ct);
        }

        int expired = await ExpirePendingActionsAsync(db, pendingCutoff, now, ct);
        int archived = await db.ChatChannels
            .Where(channel => channel.Kind == ChatChannelKind.HostToHost
                && !channel.IsArchived
                && channel.LastMessageAtUtc < hostToHostCutoff)
            .ExecuteUpdateAsync(update => update.SetProperty(channel => channel.IsArchived, true), ct);

        DateTime agentLogCutoff = now.AddDays(-30);
        int agentLogDeleted = await db.AgentActionLogs
            .Where(entry => entry.CreatedAtUtc < agentLogCutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0 || expired > 0 || archived > 0 || agentLogDeleted > 0)
        {
            logger.LogInformation(
                "Chat cleanup deleted {Deleted} messages, expired {Expired} pending actions, archived {Archived} channels, trimmed {AgentLog} agent log entries",
                deleted,
                expired,
                archived,
                agentLogDeleted);
        }
    }

    private static async Task<int> ExpirePendingActionsAsync(
        RadioDbContext db,
        DateTime pendingCutoff,
        DateTime now,
        CancellationToken ct)
    {
        List<ChatMessage> candidates = await db.ChatMessages
            .Where(message => message.ActionsJson != null && message.CreatedAtUtc < pendingCutoff)
            .ToListAsync(ct);
        int changed = 0;
        foreach (ChatMessage message in candidates)
        {
            List<ChatActionRecord> actions = ChatActionJson.Deserialize(message.ActionsJson).ToList();
            bool messageChanged = false;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].State != ChatActionState.PendingConfirmation)
                {
                    continue;
                }

                actions[i] = actions[i] with
                {
                    State = ChatActionState.Dismissed,
                    ResultSummary = "Expired after 24 hours without confirmation.",
                    CompletedAtUtc = now,
                };
                messageChanged = true;
                changed++;
            }

            if (messageChanged)
            {
                message.ActionsJson = ChatActionJson.Serialize(actions);
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }
}
