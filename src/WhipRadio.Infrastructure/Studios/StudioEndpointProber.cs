using System.Net.Http.Json;
using System.Text.Json;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// Probes studio endpoints over HTTP: runs the connection test for the studios
/// page (protocol sniffing per studio kind, API-key validation) and the short
/// runtime-readiness probe used before booking. No booking state, no database.
/// </summary>
public sealed class StudioEndpointProber(IHttpClientFactory httpClientFactory)
{
    public const string ProbeClientName = "studio-probe";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(2);

    public async Task<StudioRuntimeState> ProbeRuntimeAsync(Studio studio, CancellationToken ct)
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
