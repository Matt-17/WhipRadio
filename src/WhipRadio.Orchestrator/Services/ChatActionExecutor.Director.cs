using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteRemoveShowAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string scope = (Optional(call, "scope") ?? "slot_only").Trim().ToLowerInvariant();
        string reason = Require(call, "reason");

        if (scope == "disable_format")
        {
            Format format = await director.ResolveFormatAsync(Require(call, "format"), ct);
            if (await GateAsync(call, context, ApprovalRisk.Schedule, $"Disable format {format.Name} ({reason})", ct)
                is { } queued)
            {
                return queued;
            }

            string? disabled = await director.DisableFormatAsync(format.Id, ct);
            return disabled is null
                ? Failed(call, $"Format '{format.Name}' was not found.")
                : Succeeded(call, $"Disabled format {disabled}.");
        }

        string slotValue = Require(call, "slot");
        if (!int.TryParse(slotValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slotId))
        {
            return Failed(call, "slot must be a numeric slot id.");
        }

        if (await GateAsync(call, context, ApprovalRisk.Schedule, $"Remove scheduled slot {slotId} ({reason})", ct)
            is { } queuedSlot)
        {
            return queuedSlot;
        }

        string? removed = await director.RemoveSlotAsync(slotId, ct);
        return removed is null
            ? Failed(call, $"No scheduled slot with id {slotId} was found.")
            : Succeeded(call, $"Removed slot {removed}.");
    }

    private async Task<ChatActionRecord> ExecuteFireHostAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Moderator host = await director.ResolveHostAsync(Require(call, "host"), ct);
        string reason = Require(call, "reason");

        int? replacementId = null;
        string? replacementArg = Optional(call, "replacement");
        if (!string.IsNullOrWhiteSpace(replacementArg))
        {
            Moderator replacement = await director.ResolveHostAsync(replacementArg, ct);
            if (replacement.Id == host.Id)
            {
                return Failed(call, "The replacement cannot be the host being fired.");
            }

            replacementId = replacement.Id;
        }

        // Firing a host is authority-sensitive: the Boss approves it. Approval also
        // satisfies TOOLS.md's "last active general host" rule (the Boss is confirming).
        if (await GateAsync(call, context, ApprovalRisk.Personnel, $"Fire host {host.Name} ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Moderator? tracked = await db.Moderators.FirstOrDefaultAsync(m => m.Id == host.Id, ct);
        if (tracked is null || !tracked.IsActive)
        {
            return Failed(call, $"{host.Name} is no longer an active host.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        await HostTermination.ApplyFireAsync(db, tracked, replacementId, now, ct);
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
        await productionUpdates.PublishWeatherChangedAsync(ct);
        await notifications.PublishAsync(new StationNotification(
            "Personnel",
            "chat:FireHost",
            $"{host.Name} was let go: {reason}",
            now), ct);
        return Succeeded(call, $"{host.Name} was fired and their assignments were cleaned up.");
    }

    private static SpecialistHostRole ParseHostRole(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "news" => SpecialistHostRole.News,
            "weather" => SpecialistHostRole.Weather,
            _ => SpecialistHostRole.General,
        };
}
