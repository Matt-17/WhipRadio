using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Llm;

public sealed class OllamaModelMemoryManager(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> options,
    IDbContextFactory<RadioDbContext> dbFactory,
    ILogger<OllamaModelMemoryManager> logger)
{
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(10);

    public async Task TryPrepareForLocalGpuJobAsync(
        string? endpoint,
        bool unloadOllama,
        bool unloadLocalTts,
        CancellationToken ct)
    {
        if (!IsLocalEndpoint(endpoint))
        {
            return;
        }

        if (unloadOllama)
        {
            await TryUnloadDefaultModelAsync(ct);
        }

        if (unloadLocalTts)
        {
            await TryUnloadLocalTtsAsync(ct);
        }
    }

    public async Task TryUnloadDefaultModelAsync(CancellationToken ct)
    {
        var model = options.Value.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var client = httpClientFactory.CreateClient(TextGenerationRouter.OllamaClientName);
        await TryUnloadAsync(client, model, ct);
    }

    public async Task TryUnloadLocalTtsAsync(CancellationToken ct)
    {
        List<StudioEndpoint> booths;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Studios
                .AsNoTracking()
                .Where(s => s.IsActive
                    && s.Kind == StudioKind.VoiceBooth
                    && s.Provider == StudioProviders.LocalTts
                    && s.Url != null
                    && s.Url != "")
                .Select(s => new { s.Name, s.Url })
                .ToListAsync(ct);

            booths = rows
                .Where(row => IsLocalEndpoint(row.Url))
                .Select(row => new StudioEndpoint(row.Name, row.Url!))
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read local TTS booths for model unload.");
            return;
        }

        foreach (var booth in booths)
        {
            await TryUnloadLocalTtsAsync(booth, ct);
        }
    }

    public async Task TryUnloadAsync(HttpClient client, string model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UnloadTimeout);

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/generate",
                new UnloadRequest(model, Stream: false, KeepAlive: 0),
                timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Requested Ollama unload for model {Model}", model);
                return;
            }

            logger.LogDebug(
                "Ollama unload for model {Model} returned HTTP {StatusCode}",
                model, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Ollama unload for model {Model} timed out after {Timeout}s", model, UnloadTimeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Ollama unload for model {Model} could not reach the endpoint", model);
        }
    }

    private async Task TryUnloadLocalTtsAsync(StudioEndpoint booth, CancellationToken ct)
    {
        if (!Uri.TryCreate($"{booth.Url.TrimEnd('/')}/unload", UriKind.Absolute, out var uri))
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UnloadTimeout);

        try
        {
            var client = httpClientFactory.CreateClient(StudioProviderFactory.StudioClientName);
            using var response = await client.PostAsync(uri, content: null, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Requested TTS model unload for {Booth} at {Endpoint}", booth.Name, booth.Url);
                return;
            }

            logger.LogDebug(
                "TTS unload for {Booth} at {Endpoint} returned HTTP {StatusCode}",
                booth.Name, booth.Url, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(
                "TTS unload for {Booth} at {Endpoint} timed out after {Timeout}s",
                booth.Name, booth.Url, UnloadTimeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "TTS unload for {Booth} at {Endpoint} could not reach the endpoint", booth.Name, booth.Url);
        }
    }

    private static bool IsLocalEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
            || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UnloadRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] int KeepAlive);

    private sealed record StudioEndpoint(string Name, string Url);
}
