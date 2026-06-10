using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Music;

/// <summary>Thrown when the requested music backend is not available (sidecar returned 503).</summary>
public class MusicBackendUnavailableException(string backend)
    : Exception($"Music backend '{backend}' is unavailable.")
{
    public string Backend { get; } = backend;
}

/// <summary>Client for the music sidecar (Plan.md §7.2). Generation is long-running —
/// the named HttpClient must be configured with a generous (30 min) timeout.</summary>
public class HttpMusicGenerator(HttpClient http) : IMusicGenerator
{
    public const string BackendHeader = "X-Backend";

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct)
    {
        var backend = request.WantVocals ? MusicBackends.AceStep : MusicBackends.MusicGen;
        var body = new GenerateRequest(request.Prompt, backend, request.DurationSeconds, request.Lyrics);

        using var response = await http.PostAsJsonAsync("/generate", body, ct);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new MusicBackendUnavailableException(backend);
        }

        response.EnsureSuccessStatusCode();

        var wavData = await response.Content.ReadAsByteArrayAsync(ct);
        var backendUsed = response.Headers.TryGetValues(BackendHeader, out var values)
            ? values.FirstOrDefault() ?? backend
            : backend;

        return new MusicResult(wavData, backendUsed);
    }

    public async Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
    {
        try
        {
            var health = await http.GetFromJsonAsync<HealthResponse>("/health", ct);
            return health?.Backends?.GetValueOrDefault(backend) ?? false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    internal sealed record GenerateRequest(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("backend")] string Backend,
        [property: JsonPropertyName("duration_seconds")] int DurationSeconds,
        [property: JsonPropertyName("lyrics")] string? Lyrics);

    internal sealed record HealthResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("backends")] Dictionary<string, bool>? Backends);
}
