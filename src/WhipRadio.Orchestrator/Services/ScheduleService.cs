using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Resolves the current ShowContext from the program plan (ProgramSlot → Format
/// → host + musical direction). While a spot is still in planning, a fallback
/// rotation keeps the station alive: genres rotate hourly, active hosts take
/// 2-hour shifts.
/// </summary>
public class ScheduleService(IDbContextFactory<RadioDbContext> dbFactory, TimeProvider timeProvider)
{
    public async Task<ShowContext> GetCurrentAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetLocalNow();
        var minuteOfDay = now.Hour * 60 + now.Minute;
        var day = (int)now.DayOfWeek;

        var slot = await db.ProgramSlots
            .Include(s => s.Format!).ThenInclude(f => f.Moderator)
            .Where(s => s.DayOfWeek == day
                && s.StartMinute <= minuteOfDay
                && minuteOfDay < s.StartMinute + s.DurationMinutes)
            .OrderByDescending(s => s.StartMinute)
            .FirstOrDefaultAsync(ct);

        if (slot?.Format is { IsEnabled: true } format && format.Moderator is { IsActive: true } host)
        {
            var nextFormatName = await GetNextFormatNameAsync(db, day, minuteOfDay, ct);
            return new ShowContext(
                format.Genre,
                format.Subgenre,
                host,
                format,
                slot.StartMinute,
                slot.DurationMinutes,
                Math.Max(0, slot.StartMinute + slot.DurationMinutes - minuteOfDay),
                nextFormatName);
        }

        return await FallbackContextAsync(db, now, ct);
    }

    private static async Task<string?> GetNextFormatNameAsync(
        RadioDbContext db,
        int day,
        int minuteOfDay,
        CancellationToken ct)
    {
        var slots = await db.ProgramSlots.AsNoTracking()
            .Include(s => s.Format)
            .Where(s => s.Format != null && s.Format.IsEnabled)
            .ToListAsync(ct);

        return slots
            .Select(s =>
            {
                var dayOffset = (s.DayOfWeek - day + 7) % 7;
                var minutesAway = dayOffset * 24 * 60 + s.StartMinute - minuteOfDay;
                if (minutesAway <= 0)
                {
                    minutesAway += 7 * 24 * 60;
                }

                return new { Slot = s, MinutesAway = minutesAway };
            })
            .OrderBy(x => x.MinutesAway)
            .Select(x => x.Slot.Format?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<ShowContext> FallbackContextAsync(
        RadioDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var moderators = await db.Moderators.AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        if (moderators.Count == 0)
        {
            moderators = await db.Moderators.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        }

        // 2-hour host shifts; offset by day so the same hour isn't always the same host.
        var shiftIndex = (now.Hour / 2 + (int)now.DayOfWeek) % moderators.Count;
        var moderator = moderators[shiftIndex];

        var genre = GenreCatalog.Genres[now.Hour % GenreCatalog.Genres.Count];

        // Hosts pull the rotation toward their own taste when they have one.
        var preferred = moderator.PreferredGenres
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (preferred.Length > 0 && now.Hour % 2 == 1)
        {
            genre = preferred[now.Hour / 2 % preferred.Length];
        }

        var subgenre = GenreCatalog.PickSubgenre(genre, new Random(now.DayOfYear * 24 + now.Hour));
        var slotStartMinute = (now.Hour / 2) * 120;
        var minuteOfDay = now.Hour * 60 + now.Minute;
        return new ShowContext(
            genre,
            subgenre,
            moderator,
            SlotStartMinute: slotStartMinute,
            SlotDurationMinutes: 120,
            RemainingSlotMinutes: Math.Max(0, slotStartMinute + 120 - minuteOfDay));
    }
}
