using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Runs sidecar analysis for a freshly written WAV and upserts the
/// MediaAnalysis row. Never throws: a failed analysis stores a stub row with
/// AnalyzerVersion=0 so the item stays playable and the backfill retries later.
/// </summary>
public class MediaAnalysisRecorder(
    IAudioAnalysisClient client,
    ImportedAudioStager stager,
    IDbContextFactory<RadioDbContext> dbFactory,
    ILogger<MediaAnalysisRecorder> logger)
{
    public const int CurrentAnalyzerVersion = 1;

    public async Task AnalyzeAndStoreAsync(
        PlayoutItemType itemType, Guid itemId, string relativePath, CancellationToken ct)
    {
        MediaAnalysis row;
        try
        {
            var mode = itemType == PlayoutItemType.Announcement ? AnalysisMode.Speech : AnalysisMode.Music;
            // Imported audio (outside the data root, or MP3) is staged as a
            // temp WAV the sidecar can reach; in-root WAVs pass through.
            using var staged = await stager.StageAsync(itemId, relativePath, ct);
            var dto = await client.AnalyzeAsync(staged.SidecarRelativePath, mode, ct);
            row = Map(itemType, itemId, dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Analysis failed for {Type} {Id} ({Reason}) — storing stub, backfill will retry",
                itemType, itemId, ex.GetBaseException().Message);
            row = new MediaAnalysis
            {
                Id = Guid.NewGuid(),
                ItemType = itemType,
                ItemId = itemId,
                AnalyzerVersion = 0,
                AnalyzedAt = DateTime.UtcNow,
            };
        }

        await UpsertAsync(row, ct);
        if (itemType == PlayoutItemType.Track && row.AnalyzerVersion > 0)
        {
            await UpdateImportedDurationAsync(itemId, row.DurationSeconds, ct);
        }
    }

    /// <summary>
    /// Imported tracks start with tag-claimed durations (tags lie); the probed
    /// analysis duration is authoritative. Generated tracks are untouched.
    /// </summary>
    private async Task UpdateImportedDurationAsync(Guid trackId, double durationSeconds, CancellationToken ct)
    {
        if (durationSeconds <= 0)
        {
            return;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Tracks
                .Where(t => t.Id == trackId && t.Source != TrackSource.Generated)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DurationSeconds, durationSeconds), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update imported track duration for {TrackId}", trackId);
        }
    }

    private static MediaAnalysis Map(PlayoutItemType itemType, Guid itemId, MediaAnalysisDto dto) => new()
    {
        Id = Guid.NewGuid(),
        ItemType = itemType,
        ItemId = itemId,
        Bpm = dto.Bpm,
        BpmConfidence = dto.BpmConfidence,
        BeatGridJson = dto.Beats is { Length: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(dto.Beats)
            : null,
        IntroEndSeconds = dto.IntroEndSeconds,
        IntroConfidence = dto.IntroConfidence,
        OutroStartSeconds = dto.OutroStartSeconds,
        OutroConfidence = dto.OutroConfidence,
        LeadingSilenceSeconds = dto.LeadingSilenceSeconds,
        TrailingSilenceSeconds = dto.TrailingSilenceSeconds,
        IntegratedLufs = dto.IntegratedLufs,
        TruePeakDb = dto.TruePeakDb,
        EnergyProfileJson = System.Text.Json.JsonSerializer.Serialize(dto.EnergyProfile),
        DurationSeconds = dto.DurationSeconds,
        AnalyzerVersion = dto.AnalyzerVersion,
        AnalyzedAt = DateTime.UtcNow,
    };

    private async Task UpsertAsync(MediaAnalysis row, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.MediaAnalyses
                .FirstOrDefaultAsync(a => a.ItemType == row.ItemType && a.ItemId == row.ItemId, ct);
            if (existing is not null)
            {
                row.Id = existing.Id;
                db.Entry(existing).CurrentValues.SetValues(row);
            }
            else
            {
                db.MediaAnalyses.Add(row);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist MediaAnalysis for {Type} {Id}", row.ItemType, row.ItemId);
        }
    }
}
