using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed class TalkBreakCleanupService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    TimeProvider timeProvider,
    ILogger<TalkBreakCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TalkBreak cleanup failed");
            }

            try
            {
                await Task.Delay(CycleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunCleanupAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.Status != TalkBreakStatus.Played
                && talkBreak.ExpiresAtUtc != null
                && talkBreak.ExpiresAtUtc <= now)
            .OrderBy(talkBreak => talkBreak.ExpiresAtUtc)
            .ThenBy(talkBreak => talkBreak.CreatedAtUtc)
            .ThenBy(talkBreak => talkBreak.Id)
            .Take(50)
            .ToListAsync(ct);

        var removedAnnouncements = 0;
        foreach (var talkBreak in expired)
        {
            talkBreak.Status = TalkBreakStatus.Expired;
            foreach (var part in talkBreak.Parts)
            {
                part.Status = TalkPartStatus.Expired;
            }

            if (talkBreak.AnnouncementId is { } announcementId)
            {
                var announcement = await db.Announcements
                    .FirstOrDefaultAsync(a => a.Id == announcementId && !a.WasPlayed, ct);
                if (announcement is not null)
                {
                    DeleteAnnouncementFile(announcement.FilePath);
                    db.Announcements.Remove(announcement);
                    removedAnnouncements++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        var removedOrphans = await DeleteOrphanAnnouncementFilesAsync(db, now, ct);

        if (expired.Count > 0 || removedOrphans > 0)
        {
            logger.LogInformation(
                "TalkBreak cleanup expired {Expired} break(s), removed {Announcements} stale announcement row(s), deleted {Orphans} orphan WAV(s)",
                expired.Count,
                removedAnnouncements,
                removedOrphans);
        }
    }

    private async Task<int> DeleteOrphanAnnouncementFilesAsync(RadioDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var directory = Path.Combine(radioOptions.Value.DataRoot, "library", "announcements");
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var candidates = Directory.EnumerateFiles(directory, "*.wav")
            .Where(path => File.GetCreationTimeUtc(path).AddDays(1) <= nowUtc)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates
            .Select(path => Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();
        var existing = await db.Announcements.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => a.Id)
            .ToHashSetAsync(ct);

        var deleted = 0;
        foreach (var path in candidates)
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) || existing.Contains(id))
            {
                continue;
            }

            TryDeleteFile(path);
            deleted++;
        }

        return deleted;
    }

    private void DeleteAnnouncementFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        TryDeleteFile(Path.Combine(radioOptions.Value.DataRoot, relativePath));
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete stale announcement file {Path}", path);
        }
    }
}
