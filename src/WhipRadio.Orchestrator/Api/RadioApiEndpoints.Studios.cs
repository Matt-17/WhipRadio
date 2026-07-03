using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Slugs;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private static void MapStudios(RouteGroupBuilder api)
    {
        api.MapGet("/studios", async (RadioDbContext db, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var studios = await db.Studios.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct);
            var jobs = coordinator.ActiveJobs;
            var snapshots = await Task.WhenAll(studios.Select(async s =>
            {
                var job = jobs.TryGetValue(s.Id, out var j) ? j : null;
                var runtime = await coordinator.GetRuntimeStateAsync(s, job, ct);
                return ToStudioDto(s, job, runtime);
            }));
            var pendingOperations = coordinator.PendingOperations
                .Select(ToStudioPendingOperationDto)
                .ToList();
            return Results.Ok(new StudioOverviewDto(snapshots.ToList(), pendingOperations));
        });

        api.MapPost("/studios/test", async (TestStudioDto request, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var (ok, provider, detail) = await coordinator.TestAsync(
                ParseStudioKind(request.Kind), request.Source, request.Url, request.Provider, request.ApiKey, ct);
            return Results.Ok(new StudioTestResultDto(ok, provider, detail));
        });

        api.MapPost("/studios", async (SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var kind = ParseStudioKind(request.Kind);
            var (ok, provider, detail) = await coordinator.TestAsync(
                kind, request.Source, request.Url, request.Provider, request.ApiKey, ct);
            if (!ok)
            {
                return Results.BadRequest(detail ?? "Connection test failed.");
            }

            var isApi = string.Equals(request.Source, "api", StringComparison.OrdinalIgnoreCase);
            var count = await db.Studios.CountAsync(s => s.Kind == kind, ct);
            var studio = new Studio
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(request.Name)
                    ? DefaultStudioName(kind, count + 1)
                    : request.Name.Trim(),
                Kind = kind,
                Url = isApi ? string.Empty : request.Url!.TrimEnd('/'),
                Provider = provider!,
                ApiKey = isApi ? request.ApiKey : null,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };

            db.Studios.Add(studio);
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPut("/studios/{id:guid}", async (Guid id, SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var studio = await db.Studios.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            var (ok, provider, detail) = await coordinator.TestAsync(
                studio.Kind, request.Source, request.Url, request.Provider, request.ApiKey, ct);
            if (!ok)
            {
                return Results.BadRequest(detail ?? "Connection test failed.");
            }

            var isApi = string.Equals(request.Source, "api", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                studio.Name = request.Name.Trim();
            }

            studio.Url = isApi ? string.Empty : request.Url!.TrimEnd('/');
            studio.Provider = provider!;
            studio.ApiKey = isApi ? request.ApiKey : null;
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPost("/studios/{id:guid}/toggle", async (Guid id, RadioDbContext db,
            IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var studio = await db.Studios.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            studio.IsActive = !studio.IsActive;
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapDelete("/studios/{id:guid}", async (Guid id, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            if (coordinator.ActiveJobs.ContainsKey(id))
            {
                return Results.Conflict("Studio is recording right now — wait for the job to finish.");
            }

            var deleted = await db.Studios.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                await updatePublisher.PublishStudiosChangedAsync(ct);
            }

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/studios/{id:guid}/restart", async (Guid id, RadioDbContext db,
            StudioDockerControl dockerControl, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var studio = await db.Studios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            var (ok, detail) = await dockerControl.TryRestartAsync(
                studio, "manual restart from studios page", force: true, ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(new StudioRestartResultDto(ok, detail));
        });
    }

    private static void MapStudioHistory(RouteGroupBuilder api)
    {
        api.MapGet("/studio-history", async (
            Guid? studioId,
            string? kind,
            string? status,
            string? search,
            int? page,
            int? pageSize,
            RadioDbContext db,
            StudioCoordinator studios,
            CancellationToken ct) =>
        {
            var query = db.StudioHistory.AsNoTracking();
            if (studioId is { } id)
            {
                query = query.Where(h => h.StudioId == id);
            }

            if (!string.IsNullOrWhiteSpace(kind)
                && Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsedKind))
            {
                query = query.Where(h => h.StudioKind == parsedKind);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var trimmedStatus = status.Trim();
                query = query.Where(h => h.Status == trimmedStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(h =>
                    h.StudioName.Contains(term)
                    || h.Provider.Contains(term)
                    || h.Operation.Contains(term)
                    || h.Prompt.Contains(term)
                    || (h.Result != null && h.Result.Contains(term))
                    || (h.Detail != null && h.Detail.Contains(term))
                    || (h.Error != null && h.Error.Contains(term)));
            }

            var pageNumber = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 10, 100);
            var syntheticRows = await BuildActiveStudioHistoryRowsAsync(
                studios, db, studioId, kind, status, search, ct);
            var total = await query.CountAsync(ct) + syntheticRows.Count;
            var take = (pageNumber * size) + syntheticRows.Count;
            var entries = await query
                .OrderByDescending(h => h.StartedAtUtc)
                .Take(take)
                .ToListAsync(ct);
            var pageRows = entries
                .Select(ToStudioHistoryDto)
                .Concat(syntheticRows)
                .OrderByDescending(h => h.StartedAtUtc)
                .ThenBy(h => h.Operation)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToList();

            return Results.Ok(new PagedStudioHistoryDto(total, pageRows));
        });
    }

    private static async Task<List<StudioHistoryEntryDto>> BuildActiveStudioHistoryRowsAsync(
        StudioCoordinator coordinator,
        RadioDbContext db,
        Guid? studioId,
        string? kind,
        string? status,
        string? search,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(status)
            && !string.Equals(status.Trim(), StudioHistoryStatus.Running, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var activeJobs = coordinator.ActiveJobs.ToArray();
        if (activeJobs.Length == 0)
        {
            return [];
        }

        var activeIds = activeJobs.Select(job => job.Key).ToList();
        var persistedRunningIds = await db.StudioHistory
            .AsNoTracking()
            .Where(h => h.Status == StudioHistoryStatus.Running
                && h.StudioId != null
                && activeIds.Contains(h.StudioId.Value))
            .Select(h => h.StudioId!.Value)
            .ToListAsync(ct);
        var persistedRunningIdSet = persistedRunningIds.ToHashSet();
        var activeStudios = await db.Studios
            .AsNoTracking()
            .Where(s => activeIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var rows = new List<StudioHistoryEntryDto>();
        foreach (var (id, job) in activeJobs)
        {
            if (persistedRunningIdSet.Contains(id) || !activeStudios.TryGetValue(id, out var studio))
            {
                continue;
            }

            if (studioId is { } filteredStudioId && filteredStudioId != id)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(kind)
                && Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsedKind)
                && studio.Kind != parsedKind)
            {
                continue;
            }

            var prompt = $"Running studio job: {job.Label}";
            var detail = "Live job from the Studios overview. Prompt/result will appear when the studio writes a history row.";
            if (!MatchesHistorySearch(studio, job, prompt, detail, search))
            {
                continue;
            }

            var duration = Math.Max(0, (DateTime.UtcNow - job.StartedAtUtc).TotalSeconds);
            rows.Add(new StudioHistoryEntryDto(
                CreateActiveHistoryId(id, job.StartedAtUtc),
                id,
                studio.Name,
                studio.Kind.ToString(),
                studio.Provider,
                job.Label,
                StudioHistoryStatus.Running,
                job.StartedAtUtc,
                null,
                duration,
                Preview(prompt),
                null,
                prompt,
                null,
                detail,
                null));
        }

        return rows;
    }

    private static bool MatchesHistorySearch(
        Studio studio,
        StudioJob job,
        string prompt,
        string detail,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();
        return studio.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || studio.Provider.Contains(term, StringComparison.OrdinalIgnoreCase)
            || job.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || prompt.Contains(term, StringComparison.OrdinalIgnoreCase)
            || detail.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid CreateActiveHistoryId(Guid studioId, DateTime startedAtUtc)
    {
        var input = Encoding.UTF8.GetBytes($"{studioId:N}:{startedAtUtc.Ticks}");
        var bytes = MD5.HashData(input);
        return new Guid(bytes);
    }

    private static StudioKind ParseStudioKind(string kind)
        => Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsed) ? parsed : StudioKind.Recording;

    private static string DefaultStudioName(StudioKind kind, int number)
        => kind switch
        {
            StudioKind.WriterRoom => $"Writer Room #{number}",
            StudioKind.VoiceBooth => $"Booth #{number}",
            _ => $"Studio #{number}",
        };

    private static StudioDto ToStudioDto(Studio s, StudioJob? job, StudioRuntimeState? runtime = null)
    {
        runtime ??= job is not null
            ? new StudioRuntimeState(StudioRuntimeState.Busy, job.Label)
            : new StudioRuntimeState(s.IsActive ? StudioRuntimeState.Unknown : StudioRuntimeState.Off);

        return new StudioDto(
        s.Id, s.Name, s.Kind.ToString(), s.Url, s.Provider, s.IsActive,
        s.CreatedAt, s.LastUsedAt, s.JobsCompleted, s.JobsFailed,
        job?.Label, job?.StartedAtUtc, job?.Progress, runtime.Status, runtime.Detail);
    }

    private static StudioPendingOperationDto ToStudioPendingOperationDto(StudioPendingOperation operation)
        => new(
            operation.Id,
            operation.Kind.ToString(),
            operation.Label,
            operation.StartedAtUtc,
            operation.Status,
            operation.Detail,
            operation.Progress,
            operation.ResourceGroup,
            operation.StudioId);

    private static StudioHistoryEntryDto ToStudioHistoryDto(StudioHistoryEntry entry)
    {
        var end = entry.CompletedAtUtc ?? (entry.Status == StudioHistoryStatus.Running ? DateTime.UtcNow : null);
        var duration = end is null ? null : (double?)(end.Value - entry.StartedAtUtc).TotalSeconds;
        return new StudioHistoryEntryDto(
            entry.Id,
            entry.StudioId,
            entry.StudioName,
            entry.StudioKind.ToString(),
            entry.Provider,
            entry.Operation,
            entry.Status,
            entry.StartedAtUtc,
            entry.CompletedAtUtc,
            duration,
            Preview(entry.Prompt),
            Preview(entry.Result),
            entry.Prompt,
            entry.Result,
            entry.Detail,
            entry.Error);
    }

    private static string Preview(string? text, int max = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : $"{oneLine[..Math.Max(0, max - 3)]}...";
    }
}
