using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Resolves the current schedule slot and its (active) moderator.</summary>
public class ScheduleService(IDbContextFactory<RadioDbContext> dbFactory, TimeProvider timeProvider)
{
    public async Task<(ScheduleSlot Slot, Moderator Moderator)> GetCurrentAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hour = timeProvider.GetLocalNow().Hour;

        var slot = await db.ScheduleSlots
            .Include(s => s.Moderator)
            .FirstOrDefaultAsync(s => s.HourOfDay == hour, ct);

        var moderator = slot?.Moderator is { IsActive: true }
            ? slot.Moderator
            : await db.Moderators.Where(m => m.IsActive).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);

        moderator ??= await db.Moderators.OrderBy(m => m.Id).FirstAsync(ct);
        slot ??= new ScheduleSlot { HourOfDay = hour, Genre = "lofi", ModeratorId = moderator.Id };

        return (slot, moderator);
    }
}
