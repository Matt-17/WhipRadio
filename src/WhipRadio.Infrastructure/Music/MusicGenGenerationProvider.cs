using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Music;

public class MusicGenGenerationProvider(HttpClient http, ILogger<MusicGenGenerationProvider> logger)
    : IMusicGenerationProvider
{
    public const string BackendHeader = "X-Backend";

    public string Id => MusicBackends.MusicGen;

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken cancellationToken)
    {
        var body = new GenerateRequest(request.Prompt, MusicBackends.MusicGen, request.DurationSeconds, request.Lyrics);

        using var response = await http.PostAsJsonAsync("/generate", body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new MusicBackendUnavailableException(Id);
        }

        response.EnsureSuccessStatusCode();

        var wavData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var backendUsed = response.Headers.TryGetValues(BackendHeader, out var values)
            ? MusicBackends.Normalize(values.FirstOrDefault() ?? Id)
            : Id;

        logger.LogInformation(
            "Generated music with {Provider}; requested duration {Duration}s; result size {Bytes} bytes",
            Id, request.DurationSeconds, wavData.Length);

        return new MusicResult(wavData, backendUsed);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await http.GetFromJsonAsync<HealthResponse>("/health", cancellationToken);
            return health?.Backends?.GetValueOrDefault(MusicBackends.MusicGen) ?? false;
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

    private sealed record HealthResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("backends")] Dictionary<string, bool>? Backends);
}

/// <summary>
/// Compatibility wrapper for the legacy MusicGen sidecar client. New code should
/// use <see cref="MusicGenGenerationProvider"/> through <see cref="MusicGenerator"/>.
/// </summary>
public sealed class HttpMusicGenerator(HttpClient http)
    : MusicGenGenerationProvider(http, NullLogger<MusicGenGenerationProvider>.Instance), IMusicGenerator
{
    public Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
        => MusicBackends.Normalize(backend) == MusicBackends.MusicGen
            ? IsAvailableAsync(ct)
            : Task.FromResult(false);
}
