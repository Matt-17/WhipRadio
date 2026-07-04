using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Keeps the program grid in sync with the long-news-format settings: when enabled,
/// one news-show Format (SelectionMode.NewsShow) plus a daily ProgramSlot per
/// configured air time; when disabled, the seeded slots are removed again. Only
/// slots belonging to the seeded format (tracked via StationSettings.NewsShowFormatId)
/// are ever touched — except that a newly seeded news block, like a director-planned
/// slot, displaces whatever overlapped it (logged).
/// </summary>
public sealed class NewsShowScheduleSeeder(
    IDbContextFactory<RadioDbContext> dbFactory,
    IHubContext<RadioHub> hub,
    ILogger<NewsShowScheduleSeeder> logger)
{
    public const string FormatName = "The News Desk";

    public async Task SyncAsync(CancellationToken ct)
    {
        bool changed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is null)
            {
                return; // nothing to sync against before first seed
            }

            changed = settings.NewsLongFormatEnabled
                ? await SeedAsync(db, settings, ct)
                : await RemoveSeededSlotsAsync(db, settings, ct);

            if (changed)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        if (changed)
        {
            await NotifyScheduleChangedAsync(ct);
        }
    }

    private async Task<bool> SeedAsync(RadioDbContext db, StationSettings settings, CancellationToken ct)
    {
        var airTimes = LongFormatNewsScheduler.ParseAirTimes(settings.NewsLongFormatAirTimes);
        if (airTimes.Count == 0)
        {
            return await RemoveSeededSlotsAsync(db, settings, ct);
        }

        var duration = LongFormatNewsScheduler.NormalizeDurationMinutes(settings.NewsLongFormatDurationMinutes);
        var changed = false;

        var format = settings.NewsShowFormatId is { } formatId
            ? await db.Formats.FirstOrDefaultAsync(f => f.Id == formatId, ct)
            : null;
        if (format is null)
        {
            format = new Format
            {
                Id = Guid.NewGuid(),
                Name = FormatName,
                Description = "Scheduled long news show: headlines, chapters, and weather over a news bed.",
                Genre = "news",
                Subgenre = "news",
                Reason = "Seeded from the long news format settings.",
                IsEnabled = true,
                Talkativeness = 1.0,
                TalkDensity = 1.0,
                TalkDepth = TalkDepth.Detailed,
                CreatedAt = DateTime.UtcNow,
                SelectionRules = new FormatSelectionRules { Mode = SelectionMode.NewsShow },
            };
            db.Formats.Add(format);
            settings.NewsShowFormatId = format.Id;
            changed = true;
        }
        else if (!format.IsEnabled || format.SelectionRules.Mode != SelectionMode.NewsShow)
        {
            format.IsEnabled = true;
            format.SelectionRules.Mode = SelectionMode.NewsShow;
            changed = true;
        }

        // The grid shows the news anchor when one is resolvable; otherwise the
        // production path creates a specialist when the first package needs it.
        var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
        var presenter = ProductionSpecialistPolicy.ResolveNewsModerator(settings, moderators);
        if (presenter is not null && format.ModeratorId != presenter.Id)
        {
            format.ModeratorId = presenter.Id;
            changed = true;
        }

        var desired = new HashSet<(int Day, int StartMinute)>(
            from day in Enumerable.Range(0, 7)
            from time in airTimes
            select (day, time.Hour * 60 + time.Minute));

        var seededSlots = await db.ProgramSlots
            .Where(slot => slot.FormatId == format.Id)
            .ToListAsync(ct);

        foreach (var stale in seededSlots.Where(slot => !desired.Contains((slot.DayOfWeek, slot.StartMinute))))
        {
            db.ProgramSlots.Remove(stale);
            changed = true;
        }

        foreach (var slot in seededSlots.Where(slot =>
            desired.Contains((slot.DayOfWeek, slot.StartMinute)) && slot.DurationMinutes != duration))
        {
            slot.DurationMinutes = duration;
            changed = true;
        }

        var existing = seededSlots
            .Select(slot => (slot.DayOfWeek, slot.StartMinute))
            .ToHashSet();
        foreach (var (day, startMinute) in desired.Where(key => !existing.Contains(key)))
        {
            // A news block displaces whatever overlapped it, same as a director plan.
            var endMinute = startMinute + duration;
            var overlapping = await db.ProgramSlots
                .Where(slot => slot.DayOfWeek == day
                    && slot.FormatId != format.Id
                    && slot.StartMinute < endMinute
                    && slot.StartMinute + slot.DurationMinutes > startMinute)
                .ToListAsync(ct);
            if (overlapping.Count > 0)
            {
                logger.LogInformation(
                    "News show slot {Day} {Start} displaces {Count} overlapping slot(s)",
                    (DayOfWeek)day, Clock(startMinute), overlapping.Count);
                db.ProgramSlots.RemoveRange(overlapping);
            }

            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = day,
                StartMinute = startMinute,
                DurationMinutes = duration,
                FormatId = format.Id,
            });
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation(
                "News show schedule synced: {Times} daily, {Duration} min each",
                LongFormatNewsScheduler.FormatAirTimes(airTimes), duration);
        }

        return changed;
    }

    private async Task<bool> RemoveSeededSlotsAsync(RadioDbContext db, StationSettings settings, CancellationToken ct)
    {
        if (settings.NewsShowFormatId is not { } formatId)
        {
            return false;
        }

        var seededSlots = await db.ProgramSlots
            .Where(slot => slot.FormatId == formatId)
            .ToListAsync(ct);
        var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == formatId, ct);

        var changed = false;
        if (seededSlots.Count > 0)
        {
            db.ProgramSlots.RemoveRange(seededSlots);
            changed = true;
        }

        if (format is { IsEnabled: true })
        {
            format.IsEnabled = false;
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation("Long news format disabled: removed {Count} seeded slot(s)", seededSlots.Count);
        }

        return changed;
    }

    private async Task NotifyScheduleChangedAsync(CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ScheduleChanged", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast schedule update after news show sync");
        }
    }

    private static string Clock(int minutes)
        => $"{Math.Clamp(minutes / 60, 0, 24):00}:{Math.Clamp(minutes % 60, 0, 59):00}";
}
