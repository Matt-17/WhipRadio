using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Shared host-deactivation cleanup used by both the <c>POST /moderators/{id}/fire</c>
/// endpoint and the <c>FireHost</c> chat verb, so both take exactly one path
/// (TOOLS.md universal rule 6). Applies mutations on the caller's tracked context
/// without saving — the caller owns <c>SaveChangesAsync</c> and any broadcast.
/// </summary>
public static class HostTermination
{
    public static async Task ApplyFireAsync(
        RadioDbContext db,
        Moderator moderator,
        int? replacementHostId,
        DateTime now,
        CancellationToken ct)
    {
        int id = moderator.Id;
        moderator.IsActive = false;

        StationSettings? settings = await db.StationSettings.FindStationSettingsAsync(ct);
        if (settings is not null)
        {
            if (settings.NewsPresenterModeratorId == id)
            {
                settings.NewsPresenterModeratorId = null;
            }

            if (settings.WeatherSpecialistModeratorId == id)
            {
                settings.WeatherSpecialistModeratorId = null;
            }
        }

        // Reassign formats to an active replacement when one is given; otherwise unassign.
        int? replacement = replacementHostId is { } candidate
            && await db.Moderators.AsNoTracking().AnyAsync(m => m.Id == candidate && m.IsActive && m.Id != id, ct)
                ? candidate
                : null;
        await db.Formats
            .Where(format => format.ModeratorId == id)
            .ExecuteUpdateAsync(update => update.SetProperty(format => format.ModeratorId, replacement), ct);

        await db.ListenerMessages
            .Where(message => message.ModeratorId == id
                && (message.Status == ListenerMessageStatus.Pending || message.Status == ListenerMessageStatus.Queued))
            .ExecuteUpdateAsync(update => update.SetProperty(message => message.ModeratorId, (int?)null), ct);

        await db.TalkBits
            .Where(bit => bit.ModeratorId == id && bit.Status == TalkBitStatus.Active)
            .ExecuteUpdateAsync(update => update
                .SetProperty(bit => bit.Status, TalkBitStatus.Retired)
                .SetProperty(bit => bit.RetiredAtUtc, now)
                .SetProperty(bit => bit.RetirementReason, "Host fired"), ct);

        await db.TalkParts
            .Where(part => db.TalkBreaks.Any(talkBreak => talkBreak.Id == part.TalkBreakId
                && talkBreak.ModeratorId == id
                && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered)))
            .ExecuteUpdateAsync(update => update
                .SetProperty(part => part.Status, TalkPartStatus.Expired)
                .SetProperty(part => part.ExpiresAtUtc, now), ct);

        await db.TalkBreaks
            .Where(talkBreak => talkBreak.ModeratorId == id
                && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered))
            .ExecuteUpdateAsync(update => update
                .SetProperty(talkBreak => talkBreak.Status, TalkBreakStatus.Expired)
                .SetProperty(talkBreak => talkBreak.ExpiresAtUtc, now), ct);
    }
}
