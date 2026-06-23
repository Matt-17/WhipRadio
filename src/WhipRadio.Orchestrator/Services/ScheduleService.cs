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

    /// <summary>
    /// Resolves the current and previous show time windows (UTC) so the track
    /// selector can hard-exclude repeats and the prompt builder can show the host
    /// what already aired. Show boundaries are ProgramSlot edges (or 2-hour
    /// fallback shifts). The previous show is the immediately preceding slot in
    /// the weekly grid, wrapping across midnight/week boundaries.
    /// </summary>
    public async Task<ShowWindows> GetShowWindowsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetLocalNow();
        var minuteOfDay = now.Hour * 60 + now.Minute;
        var day = (int)now.DayOfWeek;

        var slot = await db.ProgramSlots.AsNoTracking()
            .Include(s => s.Format)
            .Where(s => s.DayOfWeek == day
                && s.StartMinute <= minuteOfDay
                && minuteOfDay < s.StartMinute + s.DurationMinutes)
            .OrderByDescending(s => s.StartMinute)
            .FirstOrDefaultAsync(ct);

        if (slot?.Format is { IsEnabled: true } format)
        {
            var currentStart = SlotStartUtc(slot.DayOfWeek, slot.StartMinute, now);
            var currentEnd = currentStart.AddMinutes(slot.DurationMinutes);
            var previous = await GetPreviousSlotAsync(db, slot.DayOfWeek, slot.StartMinute, ct);
            DateTime? prevStart;
            DateTime? prevEnd;
            string? prevName;
            if (previous is null)
            {
                prevStart = null;
                prevEnd = null;
                prevName = null;
            }
            else
            {
                prevStart = SlotStartUtc(previous.DayOfWeek, previous.StartMinute, now);
                prevEnd = prevStart.Value.AddMinutes(previous.DurationMinutes);
                prevName = previous.Format?.Name;
            }
            return new ShowWindows(currentStart, currentEnd, prevStart, prevEnd, format.Name, prevName);
        }

        // Fallback: 2-hour shifts. Current shift = (hour/2)*120; previous = current - 120.
        var shiftStartMinute = (now.Hour / 2) * 120;
        var fallbackCurrentStart = SlotStartUtc(day, shiftStartMinute, now);
        var fallbackPrevStart = fallbackCurrentStart.AddMinutes(-120);
        return new ShowWindows(
            fallbackCurrentStart,
            fallbackCurrentStart.AddMinutes(120),
            fallbackPrevStart,
            fallbackCurrentStart,
            CurrentFormatName: null,
            PreviousFormatName: null);
    }

    private static async Task<ProgramSlot?> GetPreviousSlotAsync(
        RadioDbContext db, int currentDay, int currentStartMinute, CancellationToken ct)
    {
        var currentWeekMinute = currentDay * 1440 + currentStartMinute;
        var slots = await db.ProgramSlots.AsNoTracking()
            .Include(s => s.Format)
            .Where(s => s.Format != null && s.Format.IsEnabled)
            .ToListAsync(ct);

        ProgramSlot? best = null;
        var bestWeekMinute = int.MinValue;
        foreach (var s in slots)
        {
            var weekMinute = s.DayOfWeek * 1440 + s.StartMinute;
            if (weekMinute >= currentWeekMinute)
            {
                weekMinute -= 7 * 1440; // wrap into the previous week
            }

            if (weekMinute < currentWeekMinute && weekMinute > bestWeekMinute)
            {
                bestWeekMinute = weekMinute;
                best = s;
            }
        }

        return best;
    }

    private DateTime SlotStartUtc(int dayOfWeek, int startMinute, DateTimeOffset nowLocal)
    {
        var nowWeekMinute = (int)nowLocal.DayOfWeek * 1440 + nowLocal.Hour * 60 + nowLocal.Minute;
        var slotWeekMinute = dayOfWeek * 1440 + startMinute;
        if (slotWeekMinute > nowWeekMinute)
        {
            slotWeekMinute -= 7 * 1440; // slot is in the past week
        }

        var localStart = nowLocal.DateTime.AddMinutes(slotWeekMinute - nowWeekMinute);
        return new DateTimeOffset(localStart, nowLocal.Offset).UtcDateTime;
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
