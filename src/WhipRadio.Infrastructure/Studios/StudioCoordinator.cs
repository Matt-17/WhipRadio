using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
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
/// The studio booking desk: knows which studios/booths exist (DB), which are
/// busy right now (in-memory), hands the next free one to whoever asks, and
/// keeps usage statistics. Also runs the connection test for the studios page.
/// </summary>
public class StudioCoordinator(
    IDbContextFactory<RadioDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IStudioUpdatePublisher updatePublisher,
    LocalGpuScheduler gpuScheduler,
    OllamaModelMemoryManager modelMemory,
    ILogger<StudioCoordinator> logger)
{
    public const string ProbeClientName = "studio-probe";

    private static readonly TimeSpan BookUnderTurnTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BookRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly object _bookingGate = new();
    private readonly ConcurrentDictionary<Guid, StudioJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, StudioPendingOperation> _pendingOperations = new();
    private readonly Dictionary<string, Guid> _gpuLeases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<Guid, StudioJob> ActiveJobs => _jobs;

    public IReadOnlyList<StudioPendingOperation> PendingOperations =>
        _pendingOperations.Values.OrderBy(operation => operation.StartedAtUtc).ToList();

    /// <summary>First free active studio of the kind (optionally provider-filtered), marked busy.</summary>
    public async Task<Studio?> TryAcquireAsync(
        StudioKind kind, string? requiredProvider, string jobLabel, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        foreach (var studio in candidates)
        {
            var gpuResourceGroup = GetGpuResourceGroup(studio);
            if (IsBookedOrGpuBlocked(studio))
            {
                continue;
            }

            if (!await IsRuntimeReadyAsync(studio, ct))
            {
                continue;
            }

            var job = new StudioJob(jobLabel, DateTime.UtcNow, GpuResourceGroup: gpuResourceGroup);
            if (TryBook(studio.Id, gpuResourceGroup, job))
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
        ReleaseBooking(studioId);
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

        var pendingId = await AddPendingOperationAsync(
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
            await UpdatePendingOperationAsync(
                pendingId,
                StudioPendingOperationStatus.Preparing,
                PreparingDetail(kind),
                progress: null,
                ct);

            var studio = await BookUnderTurnAsync(kind, requiredProvider, jobLabel, ct);
            if (studio is null)
            {
                await lease.DisposeAsync();
                await RemovePendingOperationAsync(pendingId, ct);
                return null;
            }

            await RemovePendingOperationAsync(pendingId, ct);
            return new GpuStudioLease(this, studio, lease);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            await RemovePendingOperationAsync(pendingId, CancellationToken.None);
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
        => await AcquireGpuTurnAsync(kind, endpointUrl, DefaultPendingLabel(kind), ct);

    public async Task<IAsyncDisposable> AcquireGpuTurnAsync(
        StudioKind kind, string? endpointUrl, string? jobLabel, CancellationToken ct)
    {
        var group = GpuGroupForEndpoint(endpointUrl);
        if (group is null)
        {
            return NoopAsyncDisposable.Instance;
        }

        var label = string.IsNullOrWhiteSpace(jobLabel) ? DefaultPendingLabel(kind) : jobLabel.Trim();
        var pendingId = await AddPendingOperationAsync(
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
            await UpdatePendingOperationAsync(
                pendingId,
                ActivePendingStatus(kind),
                "Running on default endpoint",
                progress: null,
                ct);
            return new PendingGpuTurnLease(lease, this, pendingId);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            await RemovePendingOperationAsync(pendingId, CancellationToken.None);
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
            await UpdatePendingOperationAsync(
                id,
                StudioPendingOperationStatus.Loading,
                ModelSwitchDetail(kind, lease),
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

    private async Task<Guid> AddPendingOperationAsync(
        StudioKind kind,
        string label,
        string status,
        string? detail,
        string? resourceGroup,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _pendingOperations[id] = new StudioPendingOperation(
            id,
            kind,
            label.Trim(),
            DateTime.UtcNow,
            status,
            detail,
            Progress: null,
            resourceGroup);
        await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        return id;
    }

    private async Task UpdatePendingOperationAsync(
        Guid id,
        string status,
        string? detail,
        string? progress,
        CancellationToken ct)
    {
        if (!_pendingOperations.TryGetValue(id, out var operation))
        {
            return;
        }

        _pendingOperations[id] = operation with
        {
            Status = status,
            Detail = detail,
            Progress = progress,
        };
        await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
    }

    private async Task RemovePendingOperationAsync(Guid id, CancellationToken ct)
    {
        if (_pendingOperations.TryRemove(id, out _))
        {
            await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        }
    }

    private static string DefaultPendingLabel(StudioKind kind)
        => kind switch
        {
            StudioKind.WriterRoom => "Writing text",
            StudioKind.VoiceBooth => "Voicing audio",
            _ => "Recording music",
        };

    private static string ActivePendingStatus(StudioKind kind)
        => kind == StudioKind.WriterRoom
            ? StudioPendingOperationStatus.Work
            : StudioPendingOperationStatus.Recording;

    private static string PreparingDetail(StudioKind kind)
        => $"Preparing {KindDisplayName(kind)} endpoint";

    private static string ModelSwitchDetail(StudioKind kind, LocalGpuScheduler.GpuLease lease)
    {
        var target = KindDisplayName(kind);
        return string.IsNullOrWhiteSpace(lease.PreviousAffinity)
            ? $"Loading {target} model"
            : $"Switching from {AffinityDisplayName(lease.PreviousAffinity)} to {target}";
    }

    private static string AffinityDisplayName(string affinity)
        => Enum.TryParse<StudioKind>(affinity, ignoreCase: true, out var kind)
            ? KindDisplayName(kind)
            : affinity;

    private static string KindDisplayName(StudioKind kind)
        => kind switch
        {
            StudioKind.WriterRoom => "Writer Room",
            StudioKind.VoiceBooth => "Voice Booth",
            _ => "Recording",
        };

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
        if (string.IsNullOrWhiteSpace(progress))
        {
            return;
        }

        var trimmed = progress.Trim();
        lock (_bookingGate)
        {
            if (!_jobs.TryGetValue(studioId, out var job)
                || string.Equals(job.Progress, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _jobs[studioId] = job with { Progress = trimmed };
        }

        await updatePublisher.PublishStudiosChangedAsync(ct);
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
        return candidates.Any(IsBookedOrGpuBlocked);
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

        if (TryGetGpuBlocker(studio, out var blocker))
        {
            return new StudioRuntimeState(StudioRuntimeState.Busy, $"GPU reserved by {blocker.Label}");
        }

        return await ProbeRuntimeAsync(studio, ct);
    }

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
        if (IsBookedOrGpuBlocked(studio))
        {
            return false;
        }

        var state = await ProbeRuntimeAsync(studio, ct);
        return state.Status == StudioRuntimeState.Ready;
    }

    private bool TryBook(Guid studioId, string? gpuResourceGroup, StudioJob job)
    {
        lock (_bookingGate)
        {
            if (_jobs.ContainsKey(studioId))
            {
                return false;
            }

            if (gpuResourceGroup is not null && TryGetLiveGpuLease(gpuResourceGroup, out _, out _))
            {
                return false;
            }

            _jobs[studioId] = job;
            if (gpuResourceGroup is not null)
            {
                _gpuLeases[gpuResourceGroup] = studioId;
            }

            return true;
        }
    }

    private void ReleaseBooking(Guid studioId)
    {
        lock (_bookingGate)
        {
            if (!_jobs.TryRemove(studioId, out var job))
            {
                return;
            }

            if (job.GpuResourceGroup is not null
                && _gpuLeases.TryGetValue(job.GpuResourceGroup, out var leasedStudioId)
                && leasedStudioId == studioId)
            {
                _gpuLeases.Remove(job.GpuResourceGroup);
            }
        }
    }

    private bool IsBookedOrGpuBlocked(Studio studio)
    {
        lock (_bookingGate)
        {
            if (_jobs.ContainsKey(studio.Id))
            {
                return true;
            }

            var group = GetGpuResourceGroup(studio);
            return group is not null && TryGetLiveGpuLease(group, out _, out _);
        }
    }

    private bool TryGetGpuBlocker(Studio studio, out StudioJob blocker)
    {
        blocker = default!;
        var group = GetGpuResourceGroup(studio);
        if (group is null)
        {
            return false;
        }

        lock (_bookingGate)
        {
            return TryGetLiveGpuLease(group, out _, out blocker);
        }
    }

    private bool TryGetLiveGpuLease(string group, out Guid studioId, out StudioJob job)
    {
        if (!_gpuLeases.TryGetValue(group, out studioId))
        {
            job = default!;
            return false;
        }

        if (_jobs.TryGetValue(studioId, out job!))
        {
            return true;
        }

        _gpuLeases.Remove(group);
        job = default!;
        return false;
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

    private async Task<StudioRuntimeState> ProbeRuntimeAsync(Studio studio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(studio.Url))
        {
            return new StudioRuntimeState(StudioRuntimeState.Ready, "API provider configured");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RuntimeProbeTimeout);

            var (ok, _, detail) = await TestAsync(
                studio.Kind,
                "local",
                studio.Url,
                provider: null,
                apiKey: null,
                timeout.Token);

            return ok
                ? new StudioRuntimeState(StudioRuntimeState.Ready, detail)
                : new StudioRuntimeState(StudioRuntimeState.Offline, detail ?? "Endpoint probe failed.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StudioRuntimeState(StudioRuntimeState.Offline, "Probe timed out.");
        }
        catch (Exception ex)
        {
            return new StudioRuntimeState(StudioRuntimeState.Offline, ex.GetBaseException().Message);
        }
    }

    // ---- connection test ------------------------------------------------------

    /// <summary>Probes a studio endpoint and identifies the protocol it speaks.</summary>
    public async Task<(bool Ok, string? Provider, string? Detail)> TestAsync(
        StudioKind kind, string source, string? url, string? provider, string? apiKey, CancellationToken ct)
    {
        try
        {
            if (string.Equals(source, "api", StringComparison.OrdinalIgnoreCase))
            {
                return await TestApiProviderAsync(kind, provider, apiKey, ct);
            }

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return (false, null, "A valid URL is required.");
            }

            return kind switch
            {
                StudioKind.WriterRoom => await TestLocalWriterRoomAsync(url, ct),
                StudioKind.VoiceBooth => await TestLocalBoothAsync(url, ct),
                _ => await TestLocalRecordingAsync(url, ct),
            };
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetBaseException().Message);
        }
    }

    private async Task<(bool, string?, string?)> TestApiProviderAsync(
        StudioKind kind, string? provider, string? apiKey, CancellationToken ct)
    {
        if (string.Equals(provider, StudioProviders.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            if (kind != StudioKind.WriterRoom)
            {
                return (false, null, "OpenAI is only available for writer rooms.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, null, "An API key is required.");
            }

            var openAiClient = httpClientFactory.CreateClient(ProbeClientName);
            using var openAiRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            openAiRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            using var openAiResponse = await openAiClient.SendAsync(openAiRequest, ct);

            return openAiResponse.IsSuccessStatusCode
                ? (true, StudioProviders.OpenAi, "OpenAI - key accepted")
                : (false, null, $"OpenAI rejected the key ({(int)openAiResponse.StatusCode}).");
        }

        if (!string.Equals(provider, StudioProviders.ElevenLabs, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Unknown API provider '{provider}'.");
        }

        if (kind == StudioKind.WriterRoom)
        {
            return (false, null, "Writer room API endpoints use OpenAI.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, null, "An API key is required.");
        }

        var client = httpClientFactory.CreateClient(ProbeClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user");
        request.Headers.Add("xi-api-key", apiKey);
        using var response = await client.SendAsync(request, ct);

        return response.IsSuccessStatusCode
            ? (true, StudioProviders.ElevenLabs, "ElevenLabs — key accepted")
            : (false, null, $"ElevenLabs rejected the key ({(int)response.StatusCode}).");
    }

    private async Task<(bool, string?, string?)> TestLocalWriterRoomAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeClientName);
        using var response = await client.GetAsync($"{url.TrimEnd('/')}/api/tags", ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"GET /api/tags returned {(int)response.StatusCode}.");
        }

        var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOpts, ct);
        return (true, StudioProviders.Ollama, $"Ollama - {tags?.Models.Count ?? 0} models");
    }

    private async Task<(bool, string?, string?)> TestLocalBoothAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeClientName);
        using var response = await client.GetAsync($"{url.TrimEnd('/')}/health", ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"GET /health returned {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
            ? (true, StudioProviders.LocalTts, ExtractHealthDetail(root, "TTS sidecar"))
            : (false, null, $"TTS sidecar reports status '{status ?? "unknown"}'.");
    }

    private async Task<(bool, string?, string?)> TestLocalRecordingAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeClientName);
        using var response = await client.GetAsync($"{url.TrimEnd('/')}/health", ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"GET /health returned {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ACE-Step wraps everything in { data: {...}, code, error }.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
            var version = data.TryGetProperty("version", out var v) ? v.GetString() : null;
            var fallback = $"ACE-Step{(version is null ? "" : $" {version}")}";
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                ? (true, MusicBackends.AceStep, ExtractHealthDetail(root, fallback))
                : (false, null, $"ACE-Step reports status '{status}'.");
        }

        // MusicGen sidecar: flat { status, backends: { musicgen: true } }.
        if (root.TryGetProperty("backends", out var backends)
            && backends.TryGetProperty(MusicBackends.MusicGen, out var mg) && mg.GetBoolean())
        {
            return (true, MusicBackends.MusicGen, ExtractHealthDetail(root, "MusicGen sidecar"));
        }

        return (false, null, "Endpoint answered but speaks no known studio protocol.");
    }

    private static string ExtractHealthDetail(JsonElement root, string fallback)
    {
        foreach (var element in EnumerateHealthObjects(root))
        {
            foreach (var propertyName in new[] { "label", "detail", "description" })
            {
                var value = GetStringProperty(element, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        foreach (var element in EnumerateHealthObjects(root))
        {
            foreach (var propertyName in new[] { "service", "provider", "engine", "backend", "model" })
            {
                var value = GetStringProperty(element, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return fallback;
    }

    private static IEnumerable<JsonElement> EnumerateHealthObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                yield return data;
            }
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaModelTag> Models);

    private sealed record OllamaModelTag(string Name);

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
        StudioCoordinator coordinator,
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
                await coordinator.RemovePendingOperationAsync(pendingOperationId, CancellationToken.None);
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
