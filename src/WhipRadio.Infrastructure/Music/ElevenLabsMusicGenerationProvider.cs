using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;

namespace WhipRadio.Infrastructure.Music;

/// <summary>
/// ElevenLabs Music API: prompt + length in, audio out. Requested as raw PCM
/// and wrapped into WAV so the rest of the pipeline stays format-agnostic.
/// </summary>
public sealed class ElevenLabsMusicGenerationProvider(
    HttpClient http,
    string apiKey,
    ILogger logger) : IMusicGenerationProvider
{
    private const int SampleRate = 44100;
    private const string ApiKeyHeader = "xi-api-key";

    public string Id => MusicBackends.ElevenLabs;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/user");
            request.Headers.Add(ApiKeyHeader, apiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/music?output_format=pcm_{SampleRate}")
        {
            Content = JsonContent.Create(new ComposeRequest(prompt, request.DurationSeconds * 1000)),
        };
        message.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await http.SendAsync(message, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired)
        {
            throw new MusicBackendUnavailableException(Id);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new MusicGenerationFailedException(
                Id, $"ElevenLabs music returned {(int)response.StatusCode}: {Truncate(error)}");
        }

        var pcm = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (pcm.Length == 0)
        {
            throw new MusicGenerationFailedException(Id, "ElevenLabs returned empty audio.");
        }

        var wav = WavFile.WrapPcm16(pcm, SampleRate, channels: 1);
        logger.LogInformation(
            "ElevenLabs music generated: requested {Duration}s, result {Bytes} bytes", request.DurationSeconds, wav.Length);

        return new MusicResult(wav, Id);
    }

    private static string BuildPrompt(MusicRequest request)
    {
        var parts = new List<string> { request.Prompt };
        if (!string.IsNullOrWhiteSpace(request.SubGenre))
        {
            parts.Add(request.SubGenre);
        }

        if (request.LyricsMode == LyricsMode.Instrumental)
        {
            parts.Add("instrumental, no vocals");
        }

        return string.Join(", ", parts.Distinct());
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];

    private sealed record ComposeRequest(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("music_length_ms")] int MusicLengthMs);
}
