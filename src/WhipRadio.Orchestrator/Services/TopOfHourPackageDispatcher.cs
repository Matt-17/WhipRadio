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

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        if (!settings.NewsEnabled)
        {
            return;
        }

        await MarkPlayedPackagesAsync(db, ct);

        var grace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
        var oldestValidTarget = now.AddSeconds(-grace);
        var overdue = await db.NewsPackages
            .Where(package => package.Status == NewsPackageStatus.Ready
                && package.TargetUtc < oldestValidTarget)
            .ToListAsync(ct);
        foreach (var package in overdue)
        {
            package.Status = NewsPackageStatus.Failed;
            package.FailureReason = "Missed top-of-hour grace window.";
        }

        var next = await db.NewsPackages
            .Where(package => package.Status == NewsPackageStatus.Ready
                && package.AnnouncementId != null
                && package.TargetUtc <= now
                && package.TargetUtc >= oldestValidTarget)
            .OrderBy(package => package.TargetUtc)
            .FirstOrDefaultAsync(ct);
        if (next is null)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var announcement = await db.Announcements.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == next.AnnouncementId, ct);
        if (announcement is null)
        {
            next.Status = NewsPackageStatus.Failed;
            next.FailureReason = "Package announcement is missing.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var item = new PlayoutItem(
            PlayoutItemType.Announcement,
            announcement.Id,
            announcement.FilePath,
            "Top of hour - news and weather",
            announcement.DurationSeconds,
            announcement.ModeratorId);

        if (settings.MixerEnabled)
        {
            interrupts.Schedule(new TimedPlayoutInterrupt(
                item,
                next.TargetUtc,
                TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds),
                grace));
        }
        else
        {
            playoutQueue.EnqueueFront(item);
            logger.LogInformation("Queued top-of-hour package at queue front: {AnnouncementId}", announcement.Id);
        }

        next.Status = NewsPackageStatus.Queued;
        next.QueuedAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    private static async Task MarkPlayedPackagesAsync(RadioDbContext db, CancellationToken ct)
    {
        var queued = await db.NewsPackages
            .Where(package => package.Status == NewsPackageStatus.Queued && package.AnnouncementId != null)
            .ToListAsync(ct);
        if (queued.Count == 0)
        {
            return;
        }

        var ids = queued.Select(package => package.AnnouncementId!.Value).ToList();
        var played = await db.Announcements.AsNoTracking()
            .Where(announcement => ids.Contains(announcement.Id) && announcement.WasPlayed)
            .ToDictionaryAsync(announcement => announcement.Id, ct);

        foreach (var package in queued)
        {
            if (package.AnnouncementId is { } announcementId && played.ContainsKey(announcementId))
            {
                package.Status = NewsPackageStatus.Played;
                package.PlayedAtUtc = DateTime.UtcNow;
            }
        }
    }
}
