using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Studios;

public sealed record StudioJob(
    string Label,
    DateTime StartedAtUtc,
    string? Progress = null,
    string? GpuResourceGroup = null);

public sealed record StudioRuntimeState(string Status, string? Detail = null)
{
    public const string Busy = "busy";
    public const string Offline = "offline";
    public const string Off = "off";
    public const string Ready = "ready";
    public const string Unknown = "unknown";
}

public static class StudioProviders
{
    public const string Ollama = TextProviders.Ollama;
    public const string OpenAi = TextProviders.OpenAi;
    public const string LocalTts = "local-tts";
    public const string ElevenLabs = "elevenlabs";
}

/// <summary>
/// The studio booking desk: knows which studios/booths exist (DB), hands the next
/// free one to whoever asks, and keeps usage statistics. Composes the in-memory
/// <see cref="StudioBookingRegistry"/>, the HTTP <see cref="StudioEndpointProber"/>,
/// and the UI-facing <see cref="StudioPendingOperationsTracker"/>.
/// </summary>
public class StudioCoordinator(
    IDbContextFactory<RadioDbContext> dbFactory,
    StudioBookingRegistry bookings,
    StudioEndpointProber prober,
    StudioPendingOperationsTracker pendingOperations,
    IStudioUpdatePublisher updatePublisher,
    LocalGpuScheduler gpuScheduler,
    OllamaModelMemoryManager modelMemory,
    ILogger<StudioCoordinator> logger)
{
    private static readonly TimeSpan BookUnderTurnTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BookRetryDelay = TimeSpan.FromMilliseconds(250);

    public IReadOnlyDictionary<Guid, StudioJob> ActiveJobs => bookings.ActiveJobs;

    public IReadOnlyList<StudioPendingOperation> PendingOperations => pendingOperations.PendingOperations;

    /// <summary>First free active studio of the kind (optionally provider-filtered), marked busy.</summary>
    public async Task<Studio?> TryAcquireAsync(
        StudioKind kind, string? requiredProvider, string jobLabel, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        foreach (var studio in candidates)
        {
            var gpuResourceGroup = GetGpuResourceGroup(studio);
            if (bookings.IsBookedOrGpuBlocked(studio.Id, gpuResourceGroup))
            {
                continue;
            }

            if (!await IsRuntimeReadyAsync(studio, ct))
            {
                continue;
            }

            var job = new StudioJob(jobLabel, DateTime.UtcNow, GpuResourceGroup: gpuResourceGroup);
            if (bookings.TryBook(studio.Id, gpuResourceGroup, job))
            {
                logger.LogInformation(
                    "{Studio} booked: {Job}{GpuLease}",
                    studio.Name,
                    jobLabel,
                    gpuResourceGroup is null ? "" : $" (GPU lease {gpuResourceGroup})");
                await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
                return studio;
            }
        }

        return null;
    }

    public async Task ReleaseAsync(Guid studioId, bool success, CancellationToken ct)
    {
        bookings.Release(studioId);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Studios
                .Where(s => s.Id == studioId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(x => x.JobsCompleted, x => x.JobsCompleted + (success ? 1 : 0))
                    .SetProperty(x => x.JobsFailed, x => x.JobsFailed + (success ? 0 : 1)), ct);
            await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update studio statistics for {StudioId}", studioId);
        }
    }

    /// <summary>
    /// Acquire a studio of the given kind for a GPU job, ordered by the ambient
    /// <see cref="GpuPriorityContext"/> against everyone else waiting on the same GPU.
    /// Local-GPU studios go through <see cref="LocalGpuScheduler"/> (priority → affinity →
    /// FIFO) and only unload a foreign model when actually switching engines; API studios
    /// (no GPU) book immediately as before. Returns null when no studio of the kind exists or
    /// none becomes reachable in time. Dispose the lease via
    /// <see cref="GpuStudioLease.CompleteAsync"/>.
    /// </summary>
    public async Task<GpuStudioLease?> AcquireForGpuJobAsync(
        StudioKind kind, string? requiredProvider, string jobLabel, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var group = candidates.Select(GetGpuResourceGroup).FirstOrDefault(g => g is not null);
        if (group is null)
        {
            // API-only studios do not contend for the GPU — book one directly.
            var apiStudio = await BookUnderTurnAsync(kind, requiredProvider, jobLabel, ct);
            return apiStudio is null ? null : new GpuStudioLease(this, apiStudio, gpuLease: null);
        }

        var pendingId = await pendingOperations.AddAsync(
            kind,
            jobLabel,
            StudioPendingOperationStatus.Waiting,
            "Waiting for GPU / previous studio job",
            group,
            ct);
        LocalGpuScheduler.GpuLease? lease = null;
        try
        {
            lease = await gpuScheduler.AcquireAsync(group, kind.ToString(), GpuPriorityContext.CurrentFunc, ct);
            await ApplySwitchUnloadAsync(kind, lease, pendingId, ct);
            await pendingOperations.UpdateAsync(
                pendingId,
                StudioPendingOperationStatus.Preparing,
                StudioPendingOperationsTracker.PreparingDetail(kind),
                progress: null,
                ct);

            var studio = await BookUnderTurnAsync(kind, requiredProvider, jobLabel, ct);
            if (studio is null)
            {
                await lease.DisposeAsync();
                await pendingOperations.RemoveAsync(pendingId, ct);
                return null;
            }

            await pendingOperations.RemoveAsync(pendingId, ct);
            return new GpuStudioLease(this, studio, lease);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            await pendingOperations.RemoveAsync(pendingId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Acquire a bare GPU turn for an endpoint that has no <see cref="Studio"/> row (e.g. the
    /// default Ollama client). Orders against the shared GPU like
    /// <see cref="AcquireForGpuJobAsync"/> and applies switch-based unload, but records no
    /// studio booking/stats. Dispose the returned handle (e.g. <c>await using</c>) when done.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireGpuTurnAsync(
        StudioKind kind, string? endpointUrl, CancellationToken ct)
        => await AcquireGpuTurnAsync(kind, endpointUrl, StudioPendingOperationsTracker.DefaultLabel(kind), ct);

    public async Task<IAsyncDisposable> AcquireGpuTurnAsync(
        StudioKind kind, string? endpointUrl, string? jobLabel, CancellationToken ct)
    {
        var group = GpuGroupForEndpoint(endpointUrl);
        if (group is null)
        {
            return NoopAsyncDisposable.Instance;
        }

        var label = string.IsNullOrWhiteSpace(jobLabel)
            ? StudioPendingOperationsTracker.DefaultLabel(kind)
            : jobLabel.Trim();
        var pendingId = await pendingOperations.AddAsync(
            kind,
            label,
            StudioPendingOperationStatus.Waiting,
            "Waiting for GPU / previous studio job",
            group,
            ct);
        LocalGpuScheduler.GpuLease? lease = null;
        try
        {
            lease = await gpuScheduler.AcquireAsync(group, kind.ToString(), GpuPriorityContext.CurrentFunc, ct);
            await ApplySwitchUnloadAsync(kind, lease, pendingId, ct);
            await pendingOperations.UpdateAsync(
                pendingId,
                StudioPendingOperationsTracker.ActiveStatus(kind),
                "Running on default endpoint",
                progress: null,
                ct);
            return new PendingGpuTurnLease(lease, pendingOperations, pendingId);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            await pendingOperations.RemoveAsync(pendingId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Unload the other engines' models only when switching to a different one;
    /// consecutive same-kind jobs keep the resident model loaded.</summary>
    private async Task ApplySwitchUnloadAsync(
        StudioKind kind, LocalGpuScheduler.GpuLease lease, Guid? pendingOperationId, CancellationToken ct)
    {
        if (!lease.ModelSwitch)
        {
            return;
        }

        if (pendingOperationId is { } id)
        {
            await pendingOperations.UpdateAsync(
                id,
                StudioPendingOperationStatus.Loading,
                StudioPendingOperationsTracker.ModelSwitchDetail(kind, lease),
                progress: null,
                ct);
        }

        switch (kind)
        {
            case StudioKind.WriterRoom:
                await modelMemory.TryUnloadLocalTtsAsync(ct);
                break;
            case StudioKind.VoiceBooth:
                await modelMemory.TryUnloadDefaultModelAsync(ct);
                break;
            case StudioKind.Recording:
                await modelMemory.TryUnloadDefaultModelAsync(ct);
                await modelMemory.TryUnloadLocalTtsAsync(ct);
                break;
        }
    }

    /// <summary>Book a concrete ready studio once a GPU turn is held; the turn guarantees the
    /// GPU is free, so this only absorbs a transient runtime-probe miss.</summary>
    private async Task<Studio?> BookUnderTurnAsync(
        StudioKind kind, string? requiredProvider, string jobLabel, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BookUnderTurnTimeout;
        while (true)
        {
            var studio = await TryAcquireAsync(kind, requiredProvider, jobLabel, ct);
            if (studio is not null)
            {
                return studio;
            }

            if (!await AnyActiveAsync(kind, requiredProvider, ct) || DateTime.UtcNow > deadline)
            {
                return null;
            }

            await Task.Delay(BookRetryDelay, ct);
        }
    }

    public async Task UpdateJobProgressAsync(Guid studioId, string? progress, CancellationToken ct)
    {
        if (bookings.TryUpdateProgress(studioId, progress))
        {
            await updatePublisher.PublishStudiosChangedAsync(ct);
        }
    }

    public async Task<bool> AnyActiveAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
        => (await GetActiveAsync(kind, requiredProvider, ct)).Count > 0;

    public async Task<bool> AnyAvailableAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        foreach (var studio in candidates)
        {
            if (await IsRuntimeReadyAsync(studio, ct))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> AnyBusyAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        return candidates.Any(studio => bookings.IsBookedOrGpuBlocked(studio.Id, GetGpuResourceGroup(studio)));
    }

    /// <summary>Provider of the first active recording studio — drives vocals/prompt decisions.</summary>
    public async Task<string?> GetPreferredMusicProviderAsync(CancellationToken ct)
        => (await GetActiveAsync(StudioKind.Recording, requiredProvider: null, ct)).FirstOrDefault()?.Provider;

    public async Task<Studio?> GetFirstActiveAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
        => (await GetActiveAsync(kind, requiredProvider, ct)).FirstOrDefault();

    public async Task<StudioRuntimeState> GetRuntimeStateAsync(Studio studio, StudioJob? job, CancellationToken ct)
    {
        if (!studio.IsActive)
        {
            return new StudioRuntimeState(StudioRuntimeState.Off);
        }

        if (job is not null)
        {
            return new StudioRuntimeState(StudioRuntimeState.Busy, job.Label);
        }

        if (bookings.TryGetGpuBlocker(GetGpuResourceGroup(studio), out var blocker))
        {
            return new StudioRuntimeState(StudioRuntimeState.Busy, $"GPU reserved by {blocker.Label}");
        }

        return await prober.ProbeRuntimeAsync(studio, ct);
    }

    /// <summary>Probes a studio endpoint and identifies the protocol it speaks.</summary>
    public Task<(bool Ok, string? Provider, string? Detail)> TestAsync(
        StudioKind kind, string source, string? url, string? provider, string? apiKey, CancellationToken ct)
        => prober.TestAsync(kind, source, url, provider, apiKey, ct);

    private async Task<List<Studio>> GetActiveAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Studios.AsNoTracking().Where(s => s.IsActive && s.Kind == kind);
        if (!string.IsNullOrEmpty(requiredProvider))
        {
            query = query.Where(s => s.Provider == requiredProvider);
        }

        return await query.OrderBy(s => s.CreatedAt).ToListAsync(ct);
    }

    private async Task<bool> IsRuntimeReadyAsync(Studio studio, CancellationToken ct)
    {
        if (bookings.IsBookedOrGpuBlocked(studio.Id, GetGpuResourceGroup(studio)))
        {
            return false;
        }

        var state = await prober.ProbeRuntimeAsync(studio, ct);
        return state.Status == StudioRuntimeState.Ready;
    }

    private static string? GetGpuResourceGroup(Studio studio)
        => IsLocalGpuProvider(studio.Provider) ? GpuGroupForEndpoint(studio.Url) : null;

    /// <summary>The shared-GPU lease key for a local endpoint URL (loopback/docker host all
    /// collapse to <c>gpu:local</c>), or null when the URL is missing/invalid.</summary>
    public static string? GpuGroupForEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.IsLoopback || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            ? "local"
            : uri.IdnHost.ToLowerInvariant();
        return $"gpu:{host}";
    }

    private static bool IsLocalGpuProvider(string provider)
    {
        var normalized = MusicBackends.Normalize(provider);
        return normalized is MusicBackends.AceStep or MusicBackends.MusicGen
            || string.Equals(provider, StudioProviders.Ollama, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, StudioProviders.LocalTts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A booked studio plus its (optional) GPU turn. Call <see cref="CompleteAsync"/> exactly
    /// once when the job finishes: it records studio stats and releases the GPU turn so the
    /// next waiter is admitted.
    /// </summary>
    public sealed class GpuStudioLease
    {
        private readonly StudioCoordinator _coordinator;
        private readonly LocalGpuScheduler.GpuLease? _gpuLease;
        private int _completed;

        internal GpuStudioLease(
            StudioCoordinator coordinator, Studio studio, LocalGpuScheduler.GpuLease? gpuLease)
        {
            _coordinator = coordinator;
            Studio = studio;
            _gpuLease = gpuLease;
        }

        public Studio Studio { get; }

        public async Task CompleteAsync(bool success, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                await _coordinator.ReleaseAsync(Studio.Id, success, ct);
            }
            finally
            {
                if (_gpuLease is not null)
                {
                    await _gpuLease.DisposeAsync();
                }
            }
        }
    }

    private sealed class PendingGpuTurnLease(
        LocalGpuScheduler.GpuLease gpuLease,
        StudioPendingOperationsTracker pendingOperations,
        Guid pendingOperationId)
        : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await pendingOperations.RemoveAsync(pendingOperationId, CancellationToken.None);
            }
            finally
            {
                await gpuLease.DisposeAsync();
            }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
