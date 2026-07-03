using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Last-resort audio UPS: when the live playout queue is empty at an item
/// boundary, reuse an already generated local track without touching any
/// generation, TTS, news, or analysis dependency.
/// </summary>
public sealed class EmergencyFallbackTrackService(
    IDbContextFactory<RadioDbContext> dbFactory,
    QueueStateTracker queueTracker,
    IOptions<RadioOptions> radioOptions,
    ILogger<EmergencyFallbackTrackService> logger)
{
    private const int CandidateLimit = 200;
    private const int RecentFallbackAvoidCount = 5;
    private static readonly TimeSpan FallbackHistoryWindow = TimeSpan.FromDays(30);

    public async Task<PlayoutItem?> TryCreateFallbackTrackAsync(
        PlayoutItem? justFinished,
        CancellationToken ct)
    {
        try
        {
            return await TryCreateFallbackTrackCoreAsync(justFinished, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Emergency fallback selection failed");
            return null;
        }
    }

    private async Task<PlayoutItem?> TryCreateFallbackTrackCoreAsync(
        PlayoutItem? justFinished,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var recentlyFallbackIds = await db.PlayLog.AsNoTracking()
            .Where(entry => entry.WasFallback && entry.ItemType == PlayoutItemType.Track)
            .OrderByDescending(entry => entry.PlayedAt)
            .Select(entry => entry.ItemId)
            .Take(RecentFallbackAvoidCount)
            .ToListAsync(ct);
        var recentFallbackSet = recentlyFallbackIds.ToHashSet();

        var candidates = await db.Tracks.AsNoTracking()
            .Where(track => !track.IsRetired && track.DurationSeconds > 0)
            .OrderBy(track => track.PlayCount)
            .ThenBy(track => track.CreatedAt)
            .Take(CandidateLimit)
            .Select(track => new TrackCandidate(
                track.Id,
                track.Title,
                track.FilePath,
                track.DurationSeconds,
                track.PlayCount,
                track.CreatedAt))
            .ToListAsync(ct);

        // Group only over the shortlisted candidates within a recent window instead
        // of the station's whole play-log history; anything older than the window
        // ranks as "long ago" (DateTime.MinValue) which is all the ordering needs.
        var candidateIds = candidates.Select(candidate => candidate.Id).ToList();
        var fallbackWindowStart = DateTime.UtcNow - FallbackHistoryWindow;
        var lastFallbackByTrack = await db.PlayLog.AsNoTracking()
            .Where(entry => entry.WasFallback
                && entry.ItemType == PlayoutItemType.Track
                && entry.PlayedAt >= fallbackWindowStart
                && candidateIds.Contains(entry.ItemId))
            .GroupBy(entry => entry.ItemId)
            .Select(group => new { ItemId = group.Key, LastFallbackAt = group.Max(entry => entry.PlayedAt) })
            .ToDictionaryAsync(row => row.ItemId, row => row.LastFallbackAt, ct);

        var queuedTrackIds = queueTracker.Snapshot()
            .Where(item => item.ItemType == PlayoutItemType.Track)
            .Select(item => item.ItemId)
            .ToHashSet();
        var justFinishedId = justFinished?.ItemType == PlayoutItemType.Track
            ? justFinished.ItemId
            : (Guid?)null;

        var playable = candidates
            .Where(candidate => File.Exists(Path.Combine(radioOptions.Value.DataRoot, candidate.FilePath)))
            .ToList();
        if (playable.Count == 0)
        {
            return null;
        }

        var preferred = playable
            .Where(candidate => !queuedTrackIds.Contains(candidate.Id)
                && candidate.Id != justFinishedId
                && !recentFallbackSet.Contains(candidate.Id))
            .ToList();

        if (preferred.Count == 0)
        {
            preferred = playable
                .Where(candidate => !queuedTrackIds.Contains(candidate.Id)
                    && candidate.Id != justFinishedId)
                .ToList();
        }

        if (preferred.Count == 0)
        {
            preferred = playable;
        }

        var selected = preferred
            .OrderBy(candidate => lastFallbackByTrack.GetValueOrDefault(candidate.Id, DateTime.MinValue))
            .ThenBy(candidate => candidate.PlayCount)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .First();

        logger.LogWarning(
            "Emergency fallback selected existing track \"{Title}\" because the playout queue is empty",
            selected.Title);

        return new PlayoutItem(
            PlayoutItemType.Track,
            selected.Id,
            selected.FilePath,
            selected.Title,
            selected.DurationSeconds,
            Origin: PlayoutItemOrigin.Fallback);
    }

    private sealed record TrackCandidate(
        Guid Id,
        string Title,
        string FilePath,
        double DurationSeconds,
        int PlayCount,
        DateTime CreatedAt);
}
