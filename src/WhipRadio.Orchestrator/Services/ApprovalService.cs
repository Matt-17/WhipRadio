using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Manages the Boss confirmation queue for approval-gated chat verbs. Verbs write
/// <see cref="PendingApproval"/> rows through <c>ChatActionExecutor.GateAsync</c>;
/// this service lists them, and on approve re-runs the stored verb through the same
/// executor with <see cref="ChatActionContext.ApprovalGranted"/> set — so approval
/// revalidates role and state and never bypasses validation.
/// </summary>
public sealed class ApprovalService(
    IDbContextFactory<RadioDbContext> dbFactory,
    ChatActionExecutor executor,
    ChatParticipantResolver participants,
    IHubContext<RadioHub> hub,
    INotificationBus notifications,
    TimeProvider timeProvider,
    ILogger<ApprovalService> logger)
{
    public async Task<IReadOnlyList<PendingApprovalDto>> ListAsync(CancellationToken ct)
    {
        await ExpireOverdueAsync(ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<PendingApproval> rows = await db.PendingApprovals.AsNoTracking()
            .Where(approval => approval.Status == ApprovalStatus.Pending)
            .OrderBy(approval => approval.CreatedUtc)
            .ToListAsync(ct);

        // A short tail of recently resolved approvals so the operator sees outcomes.
        List<PendingApproval> resolved = await db.PendingApprovals.AsNoTracking()
            .Where(approval => approval.Status != ApprovalStatus.Pending)
            .OrderByDescending(approval => approval.ResolvedUtc)
            .Take(10)
            .ToListAsync(ct);

        return rows.Concat(resolved).Select(ToDto).ToList();
    }

    public async Task<(bool Ok, string Message)> ApproveAsync(Guid id, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PendingApproval? approval = await db.PendingApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return (false, "That approval no longer exists.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        if (approval.Status != ApprovalStatus.Pending)
        {
            return (false, $"That approval is already {approval.Status.ToString().ToLowerInvariant()}.");
        }

        if (approval.ExpiresAtUtc <= now)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.ResolvedUtc = now;
            approval.ResultSummary = "Expired before approval.";
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
            return (false, "That approval expired before it was confirmed.");
        }

        ChatActionContext? context = await RebuildContextAsync(approval, ct);
        if (context is null)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.ResolvedUtc = now;
            approval.ResultSummary = "The requester or channel no longer exists; approval cancelled.";
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
            return (false, "The requester or channel no longer exists; the approval was cancelled.");
        }

        CharacterToolCall call = new(approval.Tool, DeserializeArgs(approval.ArgumentsJson));
        ChatActionRecord record = await executor.ExecuteAsync(call, context, ct);

        approval.Status = ApprovalStatus.Approved;
        approval.ResolvedUtc = now;
        approval.ResultSummary = record.ResultSummary;
        await db.SaveChangesAsync(ct);

        await notifications.PublishAsync(new StationNotification(
            record.State == ChatActionState.Succeeded ? "Approval" : "Failure",
            "chat:approval",
            $"Approved {approval.Tool}: {record.ResultSummary}",
            now), ct);
        await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
        return (record.State == ChatActionState.Succeeded, record.ResultSummary ?? "Done.");
    }

    public async Task<(bool Ok, string Message)> DenyAsync(Guid id, string? reason, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PendingApproval? approval = await db.PendingApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return (false, "That approval no longer exists.");
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return (false, $"That approval is already {approval.Status.ToString().ToLowerInvariant()}.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        approval.Status = ApprovalStatus.Denied;
        approval.ResolvedUtc = now;
        approval.ResultSummary = string.IsNullOrWhiteSpace(reason) ? "Denied by the Boss." : reason.Trim();
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
        return (true, "Denied.");
    }

    private async Task ExpireOverdueAsync(CancellationToken ct)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int expired = await db.PendingApprovals
            .Where(approval => approval.Status == ApprovalStatus.Pending && approval.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(approval => approval.Status, ApprovalStatus.Expired)
                    .SetProperty(approval => approval.ResolvedUtc, now)
                    .SetProperty(approval => approval.ResultSummary, "Expired without confirmation."),
                ct);
        if (expired > 0)
        {
            await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
        }
    }

    private async Task<ChatActionContext?> RebuildContextAsync(PendingApproval approval, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel? channel = await db.ChatChannels.AsNoTracking()
            .Include(c => c.Moderator)
            .Include(c => c.CounterpartModerator)
            .FirstOrDefaultAsync(c => c.Id == approval.ChannelId, ct);
        if (channel is null)
        {
            return null;
        }

        ChatParticipantRef reference = approval.RequesterKind switch
        {
            ChatParticipantKind.Host when approval.RequesterModeratorId is int moderatorId
                => ChatParticipantRef.ForHost(moderatorId),
            ChatParticipantKind.ArtistMember when approval.RequesterEntityId is Guid memberId
                => ChatParticipantRef.ForArtistMember(memberId),
            ChatParticipantKind.Guest when approval.RequesterEntityId is Guid guestId
                => ChatParticipantRef.ForGuest(guestId),
            _ => ChatParticipantRef.Director,
        };

        ChatParticipant? sender = await participants.ResolveAsync(reference, ct);
        if (sender is null)
        {
            return null;
        }

        return new ChatActionContext(channel, null, sender, approval.CorrelationId, 0)
        {
            ApprovalGranted = true,
        };
    }

    private static Dictionary<string, string> DeserializeArgs(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private PendingApprovalDto ToDto(PendingApproval approval)
        => new(
            approval.Id,
            approval.Tool,
            approval.Summary,
            approval.Risk.ToString(),
            approval.Status.ToString(),
            approval.RequesterName,
            approval.CreatedUtc,
            approval.ExpiresAtUtc,
            approval.ResolvedUtc,
            approval.ResultSummary);
}
