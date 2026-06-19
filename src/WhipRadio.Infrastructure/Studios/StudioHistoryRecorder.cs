using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Studios;

public class StudioHistoryRecorder(
    IDbContextFactory<RadioDbContext> dbFactory,
    IStudioUpdatePublisher updatePublisher,
    ILogger<StudioHistoryRecorder> logger)
{
    public Task<Guid?> BeginAsync(
        Studio studio,
        string operation,
        string prompt,
        string? detail,
        CancellationToken ct)
        => BeginAsync(
            studio.Id,
            studio.Name,
            studio.Kind,
            studio.Provider,
            operation,
            prompt,
            detail,
            ct);

    public async Task<Guid?> BeginAsync(
        Guid? studioId,
        string studioName,
        StudioKind studioKind,
        string provider,
        string operation,
        string prompt,
        string? detail,
        CancellationToken ct)
    {
        try
        {
            var entry = new StudioHistoryEntry
            {
                Id = Guid.NewGuid(),
                StudioId = studioId,
                StudioName = studioName,
                StudioKind = studioKind,
                Provider = provider,
                Operation = operation,
                Status = StudioHistoryStatus.Running,
                StartedAtUtc = DateTime.UtcNow,
                Prompt = prompt,
                Detail = detail,
            };

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.StudioHistory.Add(entry);
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
            return entry.Id;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to record studio history start for {StudioName}", studioName);
            return null;
        }
    }

    public Task CompleteAsync(Guid? id, string? result, string? detail, CancellationToken ct)
        => FinishAsync(id, StudioHistoryStatus.Succeeded, result, detail, error: null, ct);

    public Task FailAsync(Guid? id, Exception error, string? detail, CancellationToken ct)
        => FinishAsync(id, StudioHistoryStatus.Failed, result: null, detail, error.GetBaseException().Message, ct);

    private async Task FinishAsync(
        Guid? id,
        string status,
        string? result,
        string? detail,
        string? error,
        CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entry = await db.StudioHistory.FirstOrDefaultAsync(h => h.Id == id.Value, ct);
            if (entry is null)
            {
                return;
            }

            entry.Status = status;
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.Result = result;
            entry.Error = error;
            entry.Detail = MergeDetail(entry.Detail, detail);
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to update studio history entry {HistoryId}", id);
        }
    }

    private static string? MergeDetail(string? initial, string? final)
    {
        if (string.IsNullOrWhiteSpace(initial))
        {
            return string.IsNullOrWhiteSpace(final) ? null : final;
        }

        if (string.IsNullOrWhiteSpace(final))
        {
            return initial;
        }

        return $"{initial}{Environment.NewLine}{Environment.NewLine}{final}";
    }
}
