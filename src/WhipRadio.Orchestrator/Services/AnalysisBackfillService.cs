using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Serialises CPU-hungry work: music generation holds the gate while a job is
/// in flight; the analysis backfill yields to it.
/// </summary>
public class ProductionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();

    public bool IsBusy => _semaphore.CurrentCount == 0;
}

/// <summary>
/// Every 10 minutes, analyses up to 5 items lacking a current-version
/// MediaAnalysis row (legacy tracks, failed attempts, analyzer version bumps).
/// Pauses while a music generation job holds the production gate.
/// </summary>
public class AnalysisBackfillService(
    IServiceScopeFactory scopeFactory,
    ProductionGate gate,
    IDbContextFactory<RadioDbContext> dbFactory,
    ILogger<AnalysisBackfillService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(10);
    private const int BatchSize = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var analysisClient = scope.ServiceProvider.GetRequiredService<IAudioAnalysisClient>();
                if (!gate.IsBusy && await analysisClient.IsAvailableAsync(stoppingToken))
                {
                    var recorder = scope.ServiceProvider.GetRequiredService<MediaAnalysisRecorder>();
                    await BackfillBatchAsync(recorder, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis backfill cycle failed ({Reason})", ex.GetBaseException().Message);
            }

            await stoppingToken.DelayNoThrow(CycleDelay);
        }
    }

    private async Task BackfillBatchAsync(MediaAnalysisRecorder recorder, CancellationToken ct)
    {
        List<(PlayoutItemType Type, Guid Id, string Path)> pending = [];

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var currentVersion = MediaAnalysisRecorder.CurrentAnalyzerVersion;
            var analysedTrackIds = db.MediaAnalyses
                .Where(a => a.ItemType == PlayoutItemType.Track && a.AnalyzerVersion >= currentVersion)
                .Select(a => a.ItemId);
            var tracks = await db.Tracks.AsNoTracking()
                .Where(t => !t.IsRetired && !analysedTrackIds.Contains(t.Id))
                .OrderBy(t => t.CreatedAt)
                .Take(BatchSize)
                .Select(t => new { t.Id, t.FilePath })
                .ToListAsync(ct);
            pending.AddRange(tracks.Select(t => (PlayoutItemType.Track, t.Id, t.FilePath)));

            if (pending.Count < BatchSize)
            {
                var analysedAnnouncementIds = db.MediaAnalyses
                    .Where(a => a.ItemType == PlayoutItemType.Announcement && a.AnalyzerVersion >= currentVersion)
                    .Select(a => a.ItemId);
                var announcements = await db.Announcements.AsNoTracking()
                    .Where(a => !a.WasPlayed && !analysedAnnouncementIds.Contains(a.Id))
                    .OrderByDescending(a => a.CreatedAt)
                    .Take(BatchSize - pending.Count)
                    .Select(a => new { a.Id, a.FilePath })
                    .ToListAsync(ct);
                pending.AddRange(announcements.Select(a => (PlayoutItemType.Announcement, a.Id, a.FilePath)));
            }
        }

        foreach (var (type, id, path) in pending)
        {
            if (gate.IsBusy)
            {
                logger.LogDebug("Backfill yields — music generation in flight");
                return;
            }

            await recorder.AnalyzeAndStoreAsync(type, id, path, ct);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Analysis backfill: {Count} item(s) processed", pending.Count);
        }
    }
}
