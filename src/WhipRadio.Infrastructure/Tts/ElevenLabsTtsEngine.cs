using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Tts;

/// <summary>
/// ElevenLabs cloud TTS. Speech markers have no equivalent there, so pauses are
/// rendered as ellipses. Audio is requested as raw PCM and wrapped into WAV.
/// </summary>
public class ElevenLabsTtsEngine(HttpClient http, string apiKey) : ITtsEngine
{
    private const int SampleRate = 44100;
    private const string ApiKeyHeader = "xi-api-key";

    public async Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        var plainText = SpeechMarkerNormalizer.ToPlainText(markedUpText);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/text-to-speech/{options.VoiceId}?output_format=pcm_44100")
        {
            Content = JsonContent.Create(new SynthesizeRequest(plainText, "eleven_multilingual_v2")),
        };
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var pcm = await response.Content.ReadAsByteArrayAsync(ct);
        var wav = WavFile.WrapPcm16(pcm, SampleRate, channels: 1);
        return new TtsResult(wav, WavFile.GetDurationSeconds(wav));
    }

    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/voices");
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var list = await response.Content.ReadFromJsonAsync<VoicesResponse>(ct);
        return (list?.Voices ?? [])
            .Select(v => new TtsVoice(
                v.VoiceId,
                v.Labels?.GetValueOrDefault("language") ?? "en",
                (v.Labels?.GetValueOrDefault("gender") ?? "f").StartsWith('m') ? "m" : "f"))
            .ToList();
    }

    /// <summary>
    /// Designs a brand-new voice from a description (used when creating an
    /// ElevenLabs host). Falls back to null on any API mismatch — the caller then
    /// picks a premade voice instead.
    /// </summary>
    public async Task<string?> TryCreateVoiceAsync(string name, string description, string previewText, CancellationToken ct)
    {
        try
        {
            using var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/text-to-voice/create-previews")
            {
                Content = JsonContent.Create(new { voice_description = description, text = previewText }),
            };
            previewRequest.Headers.Add(ApiKeyHeader, apiKey);

            using var previewResponse = await http.SendAsync(previewRequest, ct);
            previewResponse.EnsureSuccessStatusCode();
            var previews = await previewResponse.Content.ReadFromJsonAsync<PreviewsResponse>(ct);
            var generatedId = previews?.Previews?.FirstOrDefault()?.GeneratedVoiceId;
            if (generatedId is null)
            {
                return null;
            }

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/text-to-voice/create-voice-from-preview")
            {
                Content = JsonContent.Create(new
                {
                    voice_name = name,
                    voice_description = description,
                    generated_voice_id = generatedId,
                }),
            };
            createRequest.Headers.Add(ApiKeyHeader, apiKey);

            using var createResponse = await http.SendAsync(createRequest, ct);
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<CreatedVoiceResponse>(ct);
            return created?.VoiceId;
        }
        catch
        {
            return null;
        }
    }

    internal sealed record SynthesizeRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("model_id")] string ModelId);

    internal sealed record VoicesResponse(
        [property: JsonPropertyName("voices")] IReadOnlyList<VoiceInfo>? Voices);

    internal sealed record VoiceInfo(
        [property: JsonPropertyName("voice_id")] string VoiceId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels);

    internal sealed record PreviewsResponse(
        [property: JsonPropertyName("previews")] IReadOnlyList<Preview>? Previews);

    internal sealed record Preview(
        [property: JsonPropertyName("generated_voice_id")] string? GeneratedVoiceId);

    internal sealed record CreatedVoiceResponse(
        [property: JsonPropertyName("voice_id")] string? VoiceId);
}
