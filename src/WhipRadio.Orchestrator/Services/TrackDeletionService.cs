using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public enum TrackDeletionStatus
{
    Deleted,
    Queued,
    AlreadyQueued,
    NotFound,
}

public sealed record TrackDeletionResult(TrackDeletionStatus Status, string? Title = null);

/// <summary>
/// Keeps memory-only delete requests for tracks that still have an active playback reader.
/// </summary>
public sealed class TrackDeletionService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    ILogger<TrackDeletionService> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _activeTrackRefs = [];
    private readonly HashSet<Guid> _pendingDeletes = [];

    public bool IsTrackActive(Guid trackId)
    {
        lock (_lock)
        {
            return _activeTrackRefs.ContainsKey(trackId);
        }
    }

    public bool IsPending(Guid trackId)
    {
        lock (_lock)
        {
            return _pendingDeletes.Contains(trackId);
        }
    }

    public void MarkPlaybackStarted(PlayoutItem item)
    {
        if (item.ItemType != PlayoutItemType.Track)
        {
            return;
        }

        lock (_lock)
        {
            _activeTrackRefs[item.ItemId] = _activeTrackRefs.GetValueOrDefault(item.ItemId) + 1;
        }
    }

    public async Task MarkPlaybackCompletedAsync(PlayoutItem item, CancellationToken ct)
    {
        if (item.ItemType != PlayoutItemType.Track)
        {
            return;
        }

        bool shouldDelete = false;
        lock (_lock)
        {
            if (_activeTrackRefs.TryGetValue(item.ItemId, out int count))
            {
                if (count <= 1)
                {
                    _activeTrackRefs.Remove(item.ItemId);
                    shouldDelete = _pendingDeletes.Remove(item.ItemId);
                }
                else
                {
                    _activeTrackRefs[item.ItemId] = count - 1;
                }
            }
        }

        if (!shouldDelete || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            TrackDeletionResult result = await DeleteTrackAsync(item.ItemId, ct);
            if (result.Status == TrackDeletionStatus.Deleted)
            {
                logger.LogInformation("Deleted queued track \"{Title}\" after playback completed", result.Title);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _pendingDeletes.Add(item.ItemId);
            }

            logger.LogWarning(ex, "Queued delete failed for track {TrackId}; keeping it pending", item.ItemId);
        }
    }

    public async Task<TrackDeletionResult> QueueForDeletionAsync(Guid trackId, CancellationToken ct)
    {
        string? title = await GetTrackTitleAsync(trackId, ct);
        if (title is null)
        {
            return new TrackDeletionResult(TrackDeletionStatus.NotFound);
        }

        bool added;
        lock (_lock)
        {
            added = _pendingDeletes.Add(trackId);
        }

        return new TrackDeletionResult(
            added ? TrackDeletionStatus.Queued : TrackDeletionStatus.AlreadyQueued,
            title);
    }

    public async Task<TrackDeletionResult> DeleteNowAsync(Guid trackId, CancellationToken ct)
    {
        lock (_lock)
        {
            _pendingDeletes.Remove(trackId);
        }

        return await DeleteTrackAsync(trackId, ct);
    }

    private async Task<string?> GetTrackTitleAsync(Guid trackId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(track => track.Id == trackId)
            .Select(track => track.Title)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<TrackDeletionResult> DeleteTrackAsync(Guid trackId, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Track? track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null)
        {
            return new TrackDeletionResult(TrackDeletionStatus.NotFound);
        }

        string title = track.Title;
        string filePath = track.FilePath;
        TrackSource source = track.Source;

        await db.MediaAnalyses
            .Where(a => a.ItemType == PlayoutItemType.Track && a.ItemId == trackId)
            .ExecuteDeleteAsync(ct);
        db.Tracks.Remove(track);
        await db.SaveChangesAsync(ct);

        TryDeleteAudioFile(source, filePath);
        return new TrackDeletionResult(TrackDeletionStatus.Deleted, title);
    }

    private void TryDeleteAudioFile(TrackSource source, string filePath)
    {
        string dataRoot = radioOptions.Value.DataRoot;
        // External-library files are not ours: "delete" removes only the DB
        // rows. The data-root check is a second guard against any rooted path
        // that slipped into a non-external track.
        if (source == TrackSource.External || !MediaPaths.IsUnderDataRoot(dataRoot, filePath))
        {
            logger.LogDebug("Keeping external audio file untouched: {FilePath}", filePath);
            return;
        }

        try
        {
            string absolutePath = MediaPaths.ResolveAbsolute(dataRoot, filePath);
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Track DB row is gone; leaving stray audio file {FilePath}", filePath);
        }
    }
}
