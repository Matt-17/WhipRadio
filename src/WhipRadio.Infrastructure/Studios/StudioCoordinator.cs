using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
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
    ILogger<StudioCoordinator> logger)
{
    public const string ProbeClientName = "studio-probe";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly object _bookingGate = new();
    private readonly ConcurrentDictionary<Guid, StudioJob> _jobs = new();
    private readonly Dictionary<string, Guid> _gpuLeases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<Guid, StudioJob> ActiveJobs => _jobs;

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
    {
        if (!IsLocalGpuProvider(studio.Provider)
            || string.IsNullOrWhiteSpace(studio.Url)
            || !Uri.TryCreate(studio.Url, UriKind.Absolute, out var uri))
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
}
