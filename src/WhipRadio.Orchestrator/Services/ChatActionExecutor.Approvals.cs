using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    /// <summary>
    /// Approval gate for destructive/authority-sensitive verbs. Returns a
    /// non-null "queued for approval" record when the Boss has not yet approved
    /// this exact call, so the caller must early-return it and skip the side
    /// effect. Returns null when execution may proceed (already approved).
    /// </summary>
    private async Task<ChatActionRecord?> GateAsync(
        CharacterToolCall call,
        ChatActionContext context,
        ApprovalRisk risk,
        string summary,
        CancellationToken ct)
    {
        if (context.ApprovalGranted)
        {
            return null;
        }

        await CreatePendingApprovalAsync(call.Name, call.Arguments, context, risk, summary, ct);
        return Succeeded(
            call,
            $"Queued for Boss approval: {summary}. It runs only after the Boss confirms it.");
    }

    private async Task CreatePendingApprovalAsync(
        string tool,
        IReadOnlyDictionary<string, string> arguments,
        ChatActionContext context,
        ApprovalRisk risk,
        string summary,
        CancellationToken ct)
    {
        string argumentsJson = JsonSerializer.Serialize(arguments);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Dedupe an identical pending request so repeated agent attempts don't stack.
        bool alreadyPending = await db.PendingApprovals.AnyAsync(
            approval => approval.Status == ApprovalStatus.Pending
                && approval.Tool == tool
                && approval.ArgumentsJson == argumentsJson,
            ct);
        if (!alreadyPending)
        {
            DateTime now = timeProvider.GetUtcNow().UtcDateTime;
            db.PendingApprovals.Add(new PendingApproval
            {
                Tool = tool,
                ArgumentsJson = argumentsJson,
                Summary = summary,
                Risk = risk,
                Status = ApprovalStatus.Pending,
                RequesterKind = context.Sender.Kind,
                RequesterModeratorId = context.Sender.Ref.ModeratorId,
                RequesterEntityId = context.Sender.Ref.EntityId,
                RequesterName = context.Sender.DisplayName,
                ChannelId = context.Channel.Id,
                CorrelationId = context.CorrelationId,
                CreatedUtc = now,
                ExpiresAtUtc = now.AddMinutes(60),
            });
            await db.SaveChangesAsync(ct);

            await notifications.PublishAsync(new StationNotification(
                "Approval",
                "chat:approval",
                $"Awaiting Boss approval: {summary}",
                now), ct);
        }

        await hub.Clients.All.SendAsync("ApprovalsChanged", ct);
    }

    private async Task<ChatActionRecord> ExecuteRequestBossApprovalAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string actionTool = Require(call, "actionTool");
        if (toolCatalog.GetTool(actionTool, PromptScope.Chat, context.SenderRole) is null)
        {
            return Failed(call, $"'{actionTool}' is not a tool you are allowed to request.");
        }

        Dictionary<string, string> arguments;
        string rawArgs = Optional(call, "argumentsJson") ?? "{}";
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, string>>(rawArgs)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return Failed(call, "argumentsJson must be a JSON object mapping argument names to string values.");
        }

        ApprovalRisk risk = ParseRisk(Optional(call, "risk"));
        string summary = Optional(call, "summary") ?? $"Run {actionTool}";
        await CreatePendingApprovalAsync(actionTool, arguments, context, risk, summary, ct);
        return Succeeded(call, $"Requested Boss approval to run {actionTool}.");
    }

    private static ApprovalRisk ParseRisk(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "schedule" => ApprovalRisk.Schedule,
            "personnel" => ApprovalRisk.Personnel,
            "external" => ApprovalRisk.External,
            "settings" => ApprovalRisk.Settings,
            "cost" => ApprovalRisk.Cost,
            _ => ApprovalRisk.Library,
        };
}
