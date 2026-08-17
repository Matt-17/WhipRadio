using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Lands produced podcast episodes at their slot start (clone of the
/// TopOfHourPackageDispatcher loop): a Produced scheduled ConversationSegment
/// inside the claim window is handed to the mixer's timed interrupt (or queue
/// front on the legacy path), promoted to Queued, and marked Used once its
/// wrapper announcement has aired. One-off conversations (no TargetUtc) are
/// aired manually from the Conversations page and never pass through here.
/// </summary>
public sealed class ConversationDispatcher(
    IDbContextFactory<RadioDbContext> dbFactory,
    IPlayoutQueue playoutQueue,
    TimedPlayoutInterruptService interrupts,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    ILogger<ConversationDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Conversation dispatcher failed");
            }

            await stoppingToken.DelayNoThrow(CycleDelay);
        }
    }

    internal Task RunCycleForTestsAsync(CancellationToken ct) => RunCycleAsync(ct);

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

        var changed = await MarkUsedSegmentsAsync(db, ct);

        var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
        var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
        var earliestClaimTarget = now.AddSeconds(introGrace);
        var oldestValidTarget = now.AddSeconds(-lateWindow);

        var overdue = await db.ConversationSegments
            .Where(segment => (segment.Status == ConversationStatus.Produced
                    || segment.Status == ConversationStatus.Queued)
                && segment.TargetUtc != null
                && segment.TargetUtc < oldestValidTarget)
            .ToListAsync(ct);
        foreach (var segment in overdue)
        {
            segment.Status = ConversationStatus.Failed;
            segment.FailureReason = "Missed the episode's slot window.";
            changed = true;
        }

        var next = await db.ConversationSegments
            .Where(segment => (segment.Status == ConversationStatus.Produced
                    || segment.Status == ConversationStatus.Queued)
                && segment.AnnouncementId != null
                && segment.TargetUtc != null
                && segment.TargetUtc <= earliestClaimTarget
                && segment.TargetUtc >= oldestValidTarget)
            .OrderBy(segment => segment.TargetUtc)
            .FirstOrDefaultAsync(ct);

        if (next is null)
        {
            await db.SaveChangesAsync(ct);
            if (changed)
            {
                await productionUpdates.PublishConversationsChangedAsync(ct);
            }

            return;
        }

        var announcementId = next.AnnouncementId!.Value;
        var targetUtc = next.TargetUtc!.Value;
        var duplicateGuardWindow = TimeSpan.FromSeconds(Math.Max(introGrace, lateWindow));
        if (next.Status == ConversationStatus.Queued
            && (interrupts.HasPending(announcementId, targetUtc)
                || interrupts.WasRecentlyConsumed(announcementId, targetUtc, duplicateGuardWindow, now)))
        {
            if (changed)
            {
                await db.SaveChangesAsync(ct);
                await productionUpdates.PublishConversationsChangedAsync(ct);
            }

            return;
        }

        var announcement = await db.Announcements.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == announcementId, ct);
        if (announcement is null)
        {
            next.Status = ConversationStatus.Failed;
            next.FailureReason = "Episode announcement is missing.";
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return;
        }

        // Aired exactly once: after the playback reporter flips WasPlayed the
        // episode is done — never re-arm it (same guard as the news dispatcher).
        if (announcement.WasPlayed)
        {
            if (next.Status != ConversationStatus.Used)
            {
                next.Status = ConversationStatus.Used;
                next.UsedAtUtc = now;
            }

            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
            return;
        }

        var item = new PlayoutItem(
            PlayoutItemType.Announcement,
            announcement.Id,
            announcement.FilePath,
            $"Podcast: {next.Title ?? next.Topic}",
            announcement.DurationSeconds,
            announcement.ModeratorId);

        var isFirstClaim = next.Status != ConversationStatus.Queued;
        if (settings.MixerEnabled)
        {
            interrupts.Schedule(new TimedPlayoutInterrupt(
                item,
                targetUtc,
                TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds),
                introGrace,
                lateWindow));
            if (isFirstClaim)
            {
                // The interrupt plays outside the queue; front-queued referenced
                // tracks follow it once the queue resumes.
                await EnqueueReferencedTracksAsync(db, playoutQueue, next, logger, ct);
            }
        }
        else
        {
            if (isFirstClaim)
            {
                // Queue-front is LIFO: tracks first (reversed), then the episode
                // on top, so playback runs episode -> track A -> track B -> ...
                await EnqueueReferencedTracksAsync(db, playoutQueue, next, logger, ct);
            }

            playoutQueue.EnqueueFront(item);
            logger.LogInformation("Queued podcast episode at queue front: {AnnouncementId}", announcement.Id);
        }

        if (isFirstClaim)
        {
            next.Status = ConversationStatus.Queued;
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishConversationsChangedAsync(ct);
    }

    /// <summary>
    /// Front-queues the episode's referenced tracks in reverse order so they
    /// play right after it (BriefPodcast: "talk about A, B, C" also schedules
    /// A, B, C around the segment). Missing tracks are skipped with a log line.
    /// </summary>
    internal static async Task EnqueueReferencedTracksAsync(
        RadioDbContext db,
        IPlayoutQueue playoutQueue,
        ConversationSegment segment,
        ILogger logger,
        CancellationToken ct)
    {
        List<Guid> trackIds;
        try
        {
            trackIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(segment.ReferencedTrackIdsJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (trackIds.Count == 0)
        {
            return;
        }

        var tracks = await db.Tracks.AsNoTracking()
            .Include(track => track.Artist)
            .Where(track => trackIds.Contains(track.Id) && !track.IsRetired)
            .ToDictionaryAsync(track => track.Id, ct);

        foreach (var trackId in Enumerable.Reverse(trackIds))
        {
            if (!tracks.TryGetValue(trackId, out var track))
            {
                logger.LogInformation(
                    "Referenced track {TrackId} for episode {SegmentId} no longer exists; skipping.",
                    trackId,
                    segment.Id);
                continue;
            }

            playoutQueue.EnqueueFront(new PlayoutItem(
                PlayoutItemType.Track,
                track.Id,
                track.FilePath,
                $"{track.Artist?.Name ?? "Unknown"} - {track.Title}",
                track.DurationSeconds));
        }
    }

    private static async Task<bool> MarkUsedSegmentsAsync(RadioDbContext db, CancellationToken ct)
    {
        var queued = await db.ConversationSegments
            .Where(segment => segment.Status == ConversationStatus.Queued && segment.AnnouncementId != null)
            .ToListAsync(ct);
        if (queued.Count == 0)
        {
            return false;
        }

        var ids = queued.Select(segment => segment.AnnouncementId!.Value).ToList();
        var played = await db.Announcements.AsNoTracking()
            .Where(announcement => ids.Contains(announcement.Id) && announcement.WasPlayed)
            .Select(announcement => announcement.Id)
            .ToListAsync(ct);

        var changed = false;
        foreach (var segment in queued)
        {
            if (segment.AnnouncementId is { } announcementId && played.Contains(announcementId))
            {
                segment.Status = ConversationStatus.Used;
                segment.UsedAtUtc = DateTime.UtcNow;
                changed = true;
            }
        }

        return changed;
    }
}
