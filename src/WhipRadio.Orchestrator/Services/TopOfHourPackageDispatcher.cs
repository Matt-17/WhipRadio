using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class TopOfHourPackageDispatcher(
    IDbContextFactory<RadioDbContext> dbFactory,
    IPlayoutQueue playoutQueue,
    TimedPlayoutInterruptService interrupts,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    ILogger<TopOfHourPackageDispatcher> logger) : BackgroundService
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
                logger.LogError(ex, "Top-of-hour package dispatcher failed");
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    internal Task RunCycleForTestsAsync(CancellationToken ct) => RunCycleAsync(ct);

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        if (!settings.NewsEnabled && !settings.WeatherEnabled)
        {
            return;
        }

        var changed = await MarkPlayedPackagesAsync(db, ct);

        var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
        var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
        var earliestClaimTarget = now.AddSeconds(introGrace);
        var oldestValidTarget = now.AddSeconds(-lateWindow);
        var overdue = await db.NewsPackages
            .Where(package => (package.Status == NewsPackageStatus.Ready
                    || package.Status == NewsPackageStatus.Queued)
                && package.TargetUtc < oldestValidTarget)
            .ToListAsync(ct);
        foreach (var package in overdue)
        {
            package.Status = NewsPackageStatus.Failed;
            package.FailureReason = "Missed top-of-hour late window.";
            changed = true;
        }

        var next = await db.NewsPackages
            .Where(package => (package.Status == NewsPackageStatus.Ready
                    || package.Status == NewsPackageStatus.Queued)
                && package.AnnouncementId != null
                && package.TargetUtc <= earliestClaimTarget
                && package.TargetUtc >= oldestValidTarget)
            .OrderBy(package => package.TargetUtc)
            .FirstOrDefaultAsync(ct);

        if (next is not null && next.AnnouncementId is { } announcementId)
        {
            var duplicateGuardWindow = TimeSpan.FromSeconds(
                Math.Max(introGrace, lateWindow));
            if (next.Status == NewsPackageStatus.Queued
                && (interrupts.HasPending(announcementId, next.TargetUtc)
                    || interrupts.WasRecentlyConsumed(
                        announcementId, next.TargetUtc, duplicateGuardWindow)
                    || (next.QueuedAtUtc is { } queuedAt
                        && now - queuedAt < duplicateGuardWindow)))
            {
                if (changed)
                {
                    await db.SaveChangesAsync(ct);
                    await productionUpdates.PublishNewsChangedAsync(ct);
                }

                return;
            }
        }

        if (next is null)
        {
            await db.SaveChangesAsync(ct);
            if (changed)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            return;
        }

        var announcement = await db.Announcements.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == next.AnnouncementId, ct);
        if (announcement is null)
        {
            next.Status = NewsPackageStatus.Failed;
            next.FailureReason = "Package announcement is missing.";
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return;
        }

        // The composite TalkBreak.Title is set by FinalizePackageAsync to reflect
        // the planned package variant ("Top of hour", "Weather", "News update"),
        // so we read it directly instead of guessing from AnnouncementKind.
        var talkBreak = await db.TalkBreaks.AsNoTracking()
            .Where(tb => tb.AnnouncementId == announcement.Id)
            .Select(tb => new { tb.Title, tb.Purpose })
            .FirstOrDefaultAsync(ct);
        var label = talkBreak?.Title ?? "Top of hour";

        var item = new PlayoutItem(
            PlayoutItemType.Announcement,
            announcement.Id,
            announcement.FilePath,
            label,
            announcement.DurationSeconds,
            announcement.ModeratorId);

        if (settings.MixerEnabled)
        {
            interrupts.Schedule(new TimedPlayoutInterrupt(
                item,
                next.TargetUtc,
                TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds),
                introGrace,
                lateWindow));
        }
        else
        {
            playoutQueue.EnqueueFront(item);
            logger.LogInformation("Queued top-of-hour package at queue front: {AnnouncementId}", announcement.Id);
        }

        if (next.Status != NewsPackageStatus.Queued)
        {
            next.Status = NewsPackageStatus.Queued;
            next.QueuedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private static async Task<bool> MarkPlayedPackagesAsync(RadioDbContext db, CancellationToken ct)
    {
        var queued = await db.NewsPackages
            .Where(package => package.Status == NewsPackageStatus.Queued && package.AnnouncementId != null)
            .ToListAsync(ct);
        if (queued.Count == 0)
        {
            return false;
        }

        var ids = queued.Select(package => package.AnnouncementId!.Value).ToList();
        var played = await db.Announcements.AsNoTracking()
            .Where(announcement => ids.Contains(announcement.Id) && announcement.WasPlayed)
            .ToDictionaryAsync(announcement => announcement.Id, ct);

        var changed = false;
        foreach (var package in queued)
        {
            if (package.AnnouncementId is { } announcementId && played.ContainsKey(announcementId))
            {
                package.Status = NewsPackageStatus.Played;
                package.PlayedAtUtc = DateTime.UtcNow;
                changed = true;
            }
        }

        return changed;
    }
}
