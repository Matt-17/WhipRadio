using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tts;

public sealed record DesignedVoice(string Handle, double DurationSeconds);

/// <summary>
/// Voice design/cloning — NEW capabilities beyond ITtsEngine, only spoken by
/// Qwen-capable booths. Designing is rare and heavyweight (transient 1.7B model
/// in the booth), so calls are routed to the first active local booth and may
/// take a minute on first use (model download).
/// </summary>
public interface IVoiceDesignClient
{
    Task<DesignedVoice> DesignVoiceAsync(
        string description, string gender, string language, CancellationToken ct);

    Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct);
}

public class VoiceDesignClient(
    StudioCoordinator coordinator,
    IHttpClientFactory httpClientFactory,
    ILogger<VoiceDesignClient> logger) : IVoiceDesignClient
{
    public async Task<DesignedVoice> DesignVoiceAsync(
        string description, string gender, string language, CancellationToken ct)
    {
        var booth = await GetBoothAsync(ct);
        var client = CreateClient(booth);

        logger.LogInformation("Designing voice in {Booth}: {Description}", booth.Name, description);
        using var response = await client.PostAsJsonAsync("/design-voice", new
        {
            description,
            gender,
            language,
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Voice design failed ({(int)response.StatusCode}): {Truncate(detail)}");
        }

        var result = await response.Content.ReadFromJsonAsync<DesignVoiceResponse>(ct)
            ?? throw new InvalidOperationException("Voice design returned an empty response.");

        logger.LogInformation("Voice designed: {Handle} ({Duration:F1}s preview)",
            result.Handle, result.DurationSeconds);
        return new DesignedVoice(result.Handle, result.DurationSeconds);
    }

    public async Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct)
    {
        var booth = await GetBoothAsync(ct);
        var client = CreateClient(booth);
        return await client.GetByteArrayAsync($"/voice-preview/{Uri.EscapeDataString(handle)}", ct);
    }

    private async Task<Studio> GetBoothAsync(CancellationToken ct)
        => await coordinator.GetFirstActiveAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, ct)
            ?? throw new InvalidOperationException(
                "No active local voice booth — connect one on the Studios page first.");

    private HttpClient CreateClient(Studio booth)
    {
        var client = httpClientFactory.CreateClient(StudioProviderFactory.StudioClientName);
        client.BaseAddress = new Uri(booth.Url);
        client.Timeout = TimeSpan.FromMinutes(10); // first design downloads the 1.7B model
        return client;
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];

    private sealed record DesignVoiceResponse(
        [property: JsonPropertyName("handle")] string Handle,
        [property: JsonPropertyName("duration_seconds")] double DurationSeconds);
}
