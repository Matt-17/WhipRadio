using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Studios;

public sealed record StudioJob(string Label, DateTime StartedAtUtc);

public static class StudioProviders
{
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
    ILogger<StudioCoordinator> logger)
{
    public const string ProbeClientName = "studio-probe";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, StudioJob> _jobs = new();

    public IReadOnlyDictionary<Guid, StudioJob> ActiveJobs => _jobs;

    /// <summary>First free active studio of the kind (optionally provider-filtered), marked busy.</summary>
    public async Task<Studio?> TryAcquireAsync(
        StudioKind kind, string? requiredProvider, string jobLabel, CancellationToken ct)
    {
        var candidates = await GetActiveAsync(kind, requiredProvider, ct);
        foreach (var studio in candidates)
        {
            if (_jobs.TryAdd(studio.Id, new StudioJob(jobLabel, DateTime.UtcNow)))
            {
                logger.LogInformation("{Studio} booked: {Job}", studio.Name, jobLabel);
                return studio;
            }
        }

        return null;
    }

    public async Task ReleaseAsync(Guid studioId, bool success, CancellationToken ct)
    {
        _jobs.TryRemove(studioId, out _);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Studios
                .Where(s => s.Id == studioId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(x => x.JobsCompleted, x => x.JobsCompleted + (success ? 1 : 0))
                    .SetProperty(x => x.JobsFailed, x => x.JobsFailed + (success ? 0 : 1)), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update studio statistics for {StudioId}", studioId);
        }
    }

    public async Task<bool> AnyActiveAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
        => (await GetActiveAsync(kind, requiredProvider, ct)).Count > 0;

    /// <summary>Provider of the first active recording studio — drives vocals/prompt decisions.</summary>
    public async Task<string?> GetPreferredMusicProviderAsync(CancellationToken ct)
        => (await GetActiveAsync(StudioKind.Recording, requiredProvider: null, ct)).FirstOrDefault()?.Provider;

    public async Task<Studio?> GetFirstActiveAsync(StudioKind kind, string? requiredProvider, CancellationToken ct)
        => (await GetActiveAsync(kind, requiredProvider, ct)).FirstOrDefault();

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

    // ---- connection test ------------------------------------------------------

    /// <summary>Probes a studio endpoint and identifies the protocol it speaks.</summary>
    public async Task<(bool Ok, string? Provider, string? Detail)> TestAsync(
        StudioKind kind, string source, string? url, string? provider, string? apiKey, CancellationToken ct)
    {
        try
        {
            if (string.Equals(source, "api", StringComparison.OrdinalIgnoreCase))
            {
                return await TestApiProviderAsync(provider, apiKey, ct);
            }

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return (false, null, "A valid URL is required.");
            }

            return kind == StudioKind.VoiceBooth
                ? await TestLocalBoothAsync(url, ct)
                : await TestLocalRecordingAsync(url, ct);
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetBaseException().Message);
        }
    }

    private async Task<(bool, string?, string?)> TestApiProviderAsync(
        string? provider, string? apiKey, CancellationToken ct)
    {
        if (!string.Equals(provider, StudioProviders.ElevenLabs, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Unknown API provider '{provider}'.");
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

    private async Task<(bool, string?, string?)> TestLocalBoothAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ProbeClientName);
        using var response = await client.GetAsync($"{url.TrimEnd('/')}/voices", ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"GET /voices returned {(int)response.StatusCode}.");
        }

        var voices = await response.Content.ReadFromJsonAsync<List<JsonElement>>(JsonOpts, ct);
        return (true, StudioProviders.LocalTts, $"TTS sidecar — {voices?.Count ?? 0} voices");
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
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                ? (true, MusicBackends.AceStep, $"ACE-Step{(version is null ? "" : $" {version}")}")
                : (false, null, $"ACE-Step reports status '{status}'.");
        }

        // MusicGen sidecar: flat { status, backends: { musicgen: true } }.
        if (root.TryGetProperty("backends", out var backends)
            && backends.TryGetProperty(MusicBackends.MusicGen, out var mg) && mg.GetBoolean())
        {
            return (true, MusicBackends.MusicGen, "MusicGen sidecar");
        }

        return (false, null, "Endpoint answered but speaks no known studio protocol.");
    }
}
