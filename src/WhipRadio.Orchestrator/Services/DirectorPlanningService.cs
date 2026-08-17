using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed record SlotPlanResult(
    Guid FormatId,
    int SlotId,
    string Summary);

public sealed class DirectorPlanningService(
    IDbContextFactory<RadioDbContext> dbFactory,
    SpecialistHostCreationService hostCreation,
    IHubContext<RadioHub> hub,
    INotificationBus notifications,
    ILogger<DirectorPlanningService> logger)
{
    public async Task<SlotPlanResult> PlanSlotAsync(
        DayOfWeek day,
        int startMinute,
        int durationMinutes,
        string genre,
        string? name,
        string? description,
        int? moderatorId,
        string? reason,
        CancellationToken ct)
    {
        int normalizedStart = Math.Clamp(startMinute, 0, (24 * 60) - 30);
        int normalizedDuration = Math.Clamp(durationMinutes, 30, 240);
        int endMinute = Math.Min(24 * 60, normalizedStart + normalizedDuration);
        normalizedDuration = endMinute - normalizedStart;

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Moderator moderator = await ResolveModeratorAsync(db, moderatorId, ct);
        string normalizedGenre = NormalizeGenre(genre);
        string formatName = string.IsNullOrWhiteSpace(name)
            ? $"{CultureDay(day)} {normalizedGenre} slot"
            : name.Trim();
        string formatDescription = string.IsNullOrWhiteSpace(description)
            ? $"Chat-planned {normalizedGenre} format hosted by {moderator.Name}."
            : description.Trim();

        Format? format = await db.Formats.FirstOrDefaultAsync(item =>
            item.Name.ToLower() == formatName.ToLower(), ct);
        if (format is null)
        {
            format = new Format
            {
                Id = Guid.NewGuid(),
                Name = formatName,
                Description = formatDescription,
                Genre = normalizedGenre,
                Subgenre = normalizedGenre,
                ModeratorId = moderator.Id,
                Reason = string.IsNullOrWhiteSpace(reason) ? "planned by director chat" : reason.Trim(),
                IsEnabled = true,
                Talkativeness = 0.5,
                TalkDensity = 0.5,
                TalkDepth = TalkDepth.Light,
                CreatedAt = DateTime.UtcNow,
            };
            db.Formats.Add(format);
        }
        else
        {
            format.Description = formatDescription;
            format.Genre = normalizedGenre;
            format.Subgenre = string.IsNullOrWhiteSpace(format.Subgenre) ? normalizedGenre : format.Subgenre;
            format.ModeratorId = moderator.Id;
            format.IsEnabled = true;
        }

        int dayValue = (int)day;
        List<ProgramSlot> overlapping = await db.ProgramSlots
            .Where(slot => slot.DayOfWeek == dayValue
                && slot.StartMinute < endMinute
                && slot.StartMinute + slot.DurationMinutes > normalizedStart)
            .ToListAsync(ct);
        db.ProgramSlots.RemoveRange(overlapping);

        ProgramSlot slot = new()
        {
            DayOfWeek = dayValue,
            StartMinute = normalizedStart,
            DurationMinutes = normalizedDuration,
            FormatId = format.Id,
        };
        db.ProgramSlots.Add(slot);
        await db.SaveChangesAsync(ct);

        string summary = $"{day} {Clock(normalizedStart)}-{Clock(normalizedStart + normalizedDuration)} - {format.Name} ({format.Genre}) - host {moderator.Name}";
        db.ProgramDirectorLogs.Add(new ProgramDirectorLog
        {
            Source = ProgramDirectorLogSource.Chat,
            PromptSummary = "PlanFormat",
            ActionsJson = null,
            Outcome = summary,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await NotifyScheduleChangedAsync(ct);
        await PublishDirectorNotificationAsync("PlanFormat", summary, ct);

        logger.LogInformation("Director chat planned slot: {Summary}", summary);
        return new SlotPlanResult(format.Id, slot.Id, summary);
    }

    public Task<Moderator> HireHostAsync(string brief, CancellationToken ct)
        => HireHostAsync(brief, SpecialistHostRole.General, ct);

    public async Task<Moderator> HireHostAsync(string brief, SpecialistHostRole role, CancellationToken ct)
    {
        Moderator moderator = await hostCreation.CreateAsync(role, brief, ct);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // A news/weather hire becomes the active presenter when none is set yet, so
        // the director does not have to follow up with a separate SetPresenter call.
        if (role is SpecialistHostRole.News or SpecialistHostRole.Weather)
        {
            StationSettings? settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is not null)
            {
                if (role == SpecialistHostRole.News && settings.NewsPresenterModeratorId is null)
                {
                    settings.NewsPresenterModeratorId = moderator.Id;
                }
                else if (role == SpecialistHostRole.Weather && settings.WeatherSpecialistModeratorId is null)
                {
                    settings.WeatherSpecialistModeratorId = moderator.Id;
                }
            }
        }

        db.ProgramDirectorLogs.Add(new ProgramDirectorLog
        {
            Source = ProgramDirectorLogSource.Chat,
            PromptSummary = "HireHost",
            Outcome = $"Hired {moderator.Name}; voice designed.",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await PublishDirectorNotificationAsync("HireHost", $"Hired {moderator.Name}; voice designed.", ct);
        return moderator;
    }

    /// <summary>Removes a single planned slot by id. Returns null when it does not exist.</summary>
    public async Task<string?> RemoveSlotAsync(int slotId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ProgramSlot? slot = await db.ProgramSlots
            .Include(s => s.Format)
            .FirstOrDefaultAsync(s => s.Id == slotId, ct);
        if (slot is null)
        {
            return null;
        }

        string label = $"{(DayOfWeek)slot.DayOfWeek} {Clock(slot.StartMinute)} - {slot.Format?.Name ?? "empty slot"}";
        db.ProgramSlots.Remove(slot);
        await db.SaveChangesAsync(ct);
        await NotifyScheduleChangedAsync(ct);
        await PublishDirectorNotificationAsync("RemoveShow", $"Removed slot {label}.", ct);
        return label;
    }

    /// <summary>Disables a format and removes all its planned slots. Returns null when it does not exist.</summary>
    public async Task<string?> DisableFormatAsync(Guid formatId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Format? format = await db.Formats.FirstOrDefaultAsync(f => f.Id == formatId, ct);
        if (format is null)
        {
            return null;
        }

        format.IsEnabled = false;
        int removed = await db.ProgramSlots.Where(s => s.FormatId == formatId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await NotifyScheduleChangedAsync(ct);
        await PublishDirectorNotificationAsync("RemoveShow", $"Disabled format {format.Name} and cleared {removed} slot(s).", ct);
        return $"{format.Name} ({removed} slot(s) cleared)";
    }

    public async Task AssignHostAsync(Guid formatId, int moderatorId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Format format = await db.Formats.FirstOrDefaultAsync(item => item.Id == formatId, ct)
            ?? throw new InvalidOperationException("Format was not found.");
        Moderator moderator = await db.Moderators.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == moderatorId && item.IsActive, ct)
            ?? throw new InvalidOperationException("Active host was not found.");
        format.ModeratorId = moderator.Id;
        db.ProgramDirectorLogs.Add(new ProgramDirectorLog
        {
            Source = ProgramDirectorLogSource.Chat,
            PromptSummary = "AssignHost",
            Outcome = $"Assigned {moderator.Name} to {format.Name}.",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await NotifyScheduleChangedAsync(ct);
        await PublishDirectorNotificationAsync("AssignHost", $"Assigned {moderator.Name} to {format.Name}.", ct);
    }

    public async Task<string> BuildStatusReportAsync(CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int activeHosts = await db.Moderators.AsNoTracking().CountAsync(host => host.IsActive, ct);
        int enabledFormats = await db.Formats.AsNoTracking().CountAsync(format => format.IsEnabled, ct);
        int plannedSlots = await db.ProgramSlots.AsNoTracking().CountAsync(slot => slot.FormatId != null, ct);
        int tracks = await db.Tracks.AsNoTracking().CountAsync(track => !track.IsRetired, ct);
        int pendingTalkBreaks = await db.TalkBreaks.AsNoTracking()
            .CountAsync(talkBreak => talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered, ct);
        return $"{activeHosts} active hosts, {enabledFormats} enabled formats, {plannedSlots} planned slots, {tracks} active tracks, {pendingTalkBreaks} pending talk breaks.";
    }

    public async Task<Format> ResolveFormatAsync(string value, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (Guid.TryParse(value, out Guid id))
        {
            Format? byId = await db.Formats.AsNoTracking().FirstOrDefaultAsync(format => format.Id == id, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await db.Formats.AsNoTracking()
            .OrderBy(format => format.Name)
            .FirstOrDefaultAsync(format => format.Name.ToLower() == value.Trim().ToLower(), ct)
            ?? throw new InvalidOperationException($"Format '{value}' was not found.");
    }

    public async Task<Moderator> ResolveHostAsync(string value, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (int.TryParse(value, out int id))
        {
            Moderator? byId = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(host => host.Id == id && host.IsActive, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await db.Moderators.AsNoTracking()
            .OrderBy(host => host.Name)
            .FirstOrDefaultAsync(host => host.IsActive && host.Name.ToLower() == value.Trim().ToLower(), ct)
            ?? throw new InvalidOperationException($"Active host '{value}' was not found.");
    }

    private static async Task<Moderator> ResolveModeratorAsync(
        RadioDbContext db,
        int? moderatorId,
        CancellationToken ct)
    {
        if (moderatorId is int id)
        {
            Moderator? explicitModerator = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == id && item.IsActive, ct);
            if (explicitModerator is not null)
            {
                return explicitModerator;
            }
        }

        return await db.Moderators.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .FirstAsync(ct);
    }

    private async Task NotifyScheduleChangedAsync(CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ScheduleChanged", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast schedule update to SignalR clients");
        }
    }

    private async Task PublishDirectorNotificationAsync(string source, string message, CancellationToken ct)
    {
        try
        {
            await notifications.PublishAsync(new StationNotification(
                "Director plan",
                source,
                message,
                DateTime.UtcNow), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to publish director chat notification for {Source}", source);
        }
    }

    private static string NormalizeGenre(string genre)
    {
        string candidate = string.IsNullOrWhiteSpace(genre) ? "electronic" : genre.Trim().ToLowerInvariant();
        return GenreCatalog.Genres.FirstOrDefault(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase))
            ?? GenreCatalog.Subgenres.FirstOrDefault(item => item.Value.Contains(candidate, StringComparer.OrdinalIgnoreCase)).Key
            ?? candidate;
    }

    private static string CultureDay(DayOfWeek day) => day.ToString();

    private static string Clock(int minutes)
        => $"{Math.Clamp(minutes / 60, 0, 24):00}:{Math.Clamp(minutes % 60, 0, 59):00}";
}
