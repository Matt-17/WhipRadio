using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tts;

public sealed record DesignedVoice(string Handle, double DurationSeconds);

public sealed class VoiceDesignUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Voice design/cloning: capabilities beyond ITtsEngine, only spoken by
/// Qwen-capable booths. Designing is rare and heavyweight, so calls are routed
/// through the studio coordinator and show as busy on the Studios page.
/// </summary>
public interface IVoiceDesignClient
{
    Task<DesignedVoice> DesignVoiceAsync(
        string description, string gender, string language, string? sampleText, CancellationToken ct);

    Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct);
}

public class VoiceDesignClient(
    StudioCoordinator coordinator,
    IHttpClientFactory httpClientFactory,
    ILogger<VoiceDesignClient> logger) : IVoiceDesignClient
{
    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromSeconds(3);

    public async Task<DesignedVoice> DesignVoiceAsync(
        string description, string gender, string language, string? sampleText, CancellationToken ct)
    {
        var booth = await AcquireBoothAsync("Designing artist voice", ct);
        var success = false;
        try
        {
            var client = CreateClient(booth);

            logger.LogInformation("Designing voice in {Booth}: {Description}", booth.Name, description);
            using var response = await client.PostAsJsonAsync("/design-voice", new
            {
                description,
                gender,
                language,
                sample_text = sampleText,
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
            success = true;
            return new DesignedVoice(result.Handle, result.DurationSeconds);
        }
        finally
        {
            await coordinator.ReleaseAsync(booth.Id, success, CancellationToken.None);
        }
    }

    public async Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct)
    {
        var booth = await AcquireBoothAsync("Rendering artist voice preview", ct);
        var success = false;
        try
        {
            var client = CreateClient(booth);
            var preview = await client.GetByteArrayAsync($"/voice-preview/{Uri.EscapeDataString(handle)}", ct);
            success = true;
            return preview;
        }
        finally
        {
            await coordinator.ReleaseAsync(booth.Id, success, CancellationToken.None);
        }
    }

    private async Task<Studio> AcquireBoothAsync(string jobLabel, CancellationToken ct)
    {
        while (true)
        {
            var booth = await coordinator.TryAcquireAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, jobLabel, ct);
            if (booth is not null)
            {
                return booth;
            }

            if (!await coordinator.AnyActiveAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, ct))
            {
                throw new VoiceDesignUnavailableException("No active local voice booth is ready.");
            }

            if (!await coordinator.AnyBusyAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, ct)
                && !await coordinator.AnyAvailableAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, ct))
            {
                throw new VoiceDesignUnavailableException("No reachable local voice booth is ready.");
            }

            logger.LogDebug("Waiting for a local voice booth to design voice job {JobLabel}.", jobLabel);
            await Task.Delay(AcquireRetryDelay, ct);
        }
    }

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
