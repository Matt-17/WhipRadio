using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Keeps the program grid in sync with the podcast shows (sibling of
/// <see cref="NewsShowScheduleSeeder"/>): each enabled show owns one Format
/// (SelectionMode.PodcastShow) plus one weekly ProgramSlot; disabling or
/// deleting a show removes exactly its seeded slot and disables its format.
/// A newly seeded podcast block displaces whatever overlapped it (logged),
/// same as director-planned slots.
/// </summary>
public sealed class PodcastShowScheduleSeeder(
    IDbContextFactory<RadioDbContext> dbFactory,
    IHubContext<RadioHub> hub,
    ILogger<PodcastShowScheduleSeeder> logger)
{
    public async Task SyncAsync(CancellationToken ct)
    {
        bool changed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            changed = false;
            var shows = await db.PodcastShows.ToListAsync(ct);
            foreach (var show in shows)
            {
                changed |= show.IsEnabled
                    ? await SeedShowAsync(db, show, ct)
                    : await RemoveShowSlotsAsync(db, show, ct);
            }

            // Formats orphaned by deleted shows: their seeded slots must not survive.
            var ownedFormatIds = shows
                .Where(show => show.FormatId is not null)
                .Select(show => show.FormatId!.Value)
                .ToList();
            var orphanedSlots = await db.ProgramSlots
                .Where(slot => slot.FormatId != null
                    && slot.Format != null
                    && slot.Format.SelectionRules.Mode == SelectionMode.PodcastShow
                    && !ownedFormatIds.Contains(slot.FormatId.Value))
                .ToListAsync(ct);
            if (orphanedSlots.Count > 0)
            {
                db.ProgramSlots.RemoveRange(orphanedSlots);
                changed = true;
            }

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

    private async Task<bool> SeedShowAsync(RadioDbContext db, PodcastShow show, CancellationToken ct)
    {
        var changed = false;
        var episodeMinutes = PodcastShowScheduler.NormalizeEpisodeMinutes(show.EpisodeMinutes);
        var slotMinutes = PodcastShowScheduler.NormalizeSlotMinutes(show.SlotDurationMinutes, episodeMinutes);

        var format = show.FormatId is { } formatId
            ? await db.Formats.FirstOrDefaultAsync(f => f.Id == formatId, ct)
            : null;
        if (format is null)
        {
            format = new Format
            {
                Id = Guid.NewGuid(),
                Name = show.Name,
                Description = string.IsNullOrWhiteSpace(show.Brief)
                    ? $"Recurring podcast show \"{show.Name}\"."
                    : show.Brief,
                Genre = "talk",
                Subgenre = "podcast",
                Reason = "Seeded from a podcast show definition.",
                IsEnabled = true,
                Talkativeness = 1.0,
                TalkDensity = 1.0,
                TalkDepth = TalkDepth.DeepDive,
                CreatedAt = DateTime.UtcNow,
                SelectionRules = new FormatSelectionRules { Mode = SelectionMode.PodcastShow },
            };
            db.Formats.Add(format);
            show.FormatId = format.Id;
            changed = true;
        }
        else if (!format.IsEnabled
            || format.SelectionRules.Mode != SelectionMode.PodcastShow
            || format.Name != show.Name)
        {
            format.IsEnabled = true;
            format.SelectionRules.Mode = SelectionMode.PodcastShow;
            format.Name = show.Name;
            changed = true;
        }

        // The grid shows the show's lead host when one is resolvable.
        var leadModeratorId = ResolveLeadModeratorId(show.ParticipantsJson);
        if (leadModeratorId is { } moderatorId
            && format.ModeratorId != moderatorId
            && await db.Moderators.AnyAsync(m => m.Id == moderatorId, ct))
        {
            format.ModeratorId = moderatorId;
            changed = true;
        }

        var slots = await db.ProgramSlots
            .Where(slot => slot.FormatId == format.Id)
            .ToListAsync(ct);
        var stale = slots
            .Where(slot => slot.DayOfWeek != show.DayOfWeek || slot.StartMinute != show.StartMinute)
            .ToList();
        if (stale.Count > 0)
        {
            db.ProgramSlots.RemoveRange(stale);
            changed = true;
        }

        var current = slots.FirstOrDefault(slot =>
            slot.DayOfWeek == show.DayOfWeek && slot.StartMinute == show.StartMinute);
        if (current is null)
        {
            var endMinute = show.StartMinute + slotMinutes;
            var overlapping = await db.ProgramSlots
                .Where(slot => slot.DayOfWeek == show.DayOfWeek
                    && slot.FormatId != format.Id
                    && slot.StartMinute < endMinute
                    && slot.StartMinute + slot.DurationMinutes > show.StartMinute)
                .ToListAsync(ct);
            if (overlapping.Count > 0)
            {
                logger.LogInformation(
                    "Podcast slot \"{Show}\" displaces {Count} overlapping slot(s)", show.Name, overlapping.Count);
                db.ProgramSlots.RemoveRange(overlapping);
            }

            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = show.DayOfWeek,
                StartMinute = show.StartMinute,
                DurationMinutes = slotMinutes,
                FormatId = format.Id,
            });
            changed = true;
            logger.LogInformation(
                "Podcast show \"{Show}\" seeded: {Day} {Start}, {Slot} min slot, {Episode} min episode",
                show.Name, (DayOfWeek)show.DayOfWeek, Clock(show.StartMinute), slotMinutes, episodeMinutes);
        }
        else if (current.DurationMinutes != slotMinutes)
        {
            current.DurationMinutes = slotMinutes;
            changed = true;
        }

        return changed;
    }

    private static async Task<bool> RemoveShowSlotsAsync(RadioDbContext db, PodcastShow show, CancellationToken ct)
    {
        if (show.FormatId is not { } formatId)
        {
            return false;
        }

        var changed = false;
        var slots = await db.ProgramSlots.Where(slot => slot.FormatId == formatId).ToListAsync(ct);
        if (slots.Count > 0)
        {
            db.ProgramSlots.RemoveRange(slots);
            changed = true;
        }

        var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == formatId, ct);
        if (format is { IsEnabled: true })
        {
            format.IsEnabled = false;
            changed = true;
        }

        return changed;
    }

    private static int? ResolveLeadModeratorId(string participantsJson)
    {
        try
        {
            var participants = JsonSerializer.Deserialize<List<ConversationParticipant>>(participantsJson) ?? [];
            foreach (var participant in participants)
            {
                if (participant.TryGetModeratorId(out var moderatorId))
                {
                    return moderatorId;
                }
            }
        }
        catch (JsonException)
        {
            // malformed roster JSON — the grid simply shows no host
        }

        return null;
    }

    private async Task NotifyScheduleChangedAsync(CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ScheduleChanged", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast schedule update after podcast show sync");
        }
    }

    private static string Clock(int minutes)
        => $"{Math.Clamp(minutes / 60, 0, 24):00}:{Math.Clamp(minutes % 60, 0, 59):00}";
}
