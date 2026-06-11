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
            var dto = await client.AnalyzeAsync(relativePath, mode, ct);
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
