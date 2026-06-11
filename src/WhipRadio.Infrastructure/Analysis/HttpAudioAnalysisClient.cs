using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WhipRadio.Infrastructure.Analysis;

public enum AnalysisMode
{
    Music,
    Speech,
}

/// <summary>Wire result of the analysis sidecar (§4.1 of the mixer plan).</summary>
public sealed record MediaAnalysisDto(
    [property: JsonPropertyName("bpm")] double? Bpm,
    [property: JsonPropertyName("bpm_confidence")] double BpmConfidence,
    [property: JsonPropertyName("beats")] double[]? Beats,
    [property: JsonPropertyName("intro_end_seconds")] double? IntroEndSeconds,
    [property: JsonPropertyName("intro_confidence")] double IntroConfidence,
    [property: JsonPropertyName("outro_start_seconds")] double? OutroStartSeconds,
    [property: JsonPropertyName("outro_confidence")] double OutroConfidence,
    [property: JsonPropertyName("leading_silence_seconds")] double LeadingSilenceSeconds,
    [property: JsonPropertyName("trailing_silence_seconds")] double TrailingSilenceSeconds,
    [property: JsonPropertyName("integrated_lufs")] double IntegratedLufs,
    [property: JsonPropertyName("true_peak_db")] double TruePeakDb,
    [property: JsonPropertyName("energy_profile")] double[] EnergyProfile,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    [property: JsonPropertyName("analyzer_version")] int AnalyzerVersion,
    [property: JsonPropertyName("mode")] string Mode);

public interface IAudioAnalysisClient
{
    Task<MediaAnalysisDto> AnalyzeAsync(string relativePath, AnalysisMode mode, CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public class HttpAudioAnalysisClient(HttpClient http, ILogger<HttpAudioAnalysisClient> logger) : IAudioAnalysisClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<MediaAnalysisDto> AnalyzeAsync(string relativePath, AnalysisMode mode, CancellationToken ct)
    {
        // The sidecar sees the data volume at /data — forward slashes only.
        var body = new
        {
            path = relativePath.Replace('\\', '/'),
            mode = mode == AnalysisMode.Speech ? "speech" : "music",
        };

        using var response = await http.PostAsJsonAsync("/analyze", body, ct);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<MediaAnalysisDto>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Analysis sidecar returned an empty response.");

        logger.LogDebug(
            "Analyzed {Path}: {Bpm} BPM (conf {Conf:F2}), {Lufs} LUFS, intro {Intro}s",
            relativePath, dto.Bpm, dto.BpmConfidence, dto.IntegratedLufs, dto.IntroEndSeconds);
        return dto;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
