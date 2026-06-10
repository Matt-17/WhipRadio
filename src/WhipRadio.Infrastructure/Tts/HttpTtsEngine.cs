using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;

namespace WhipRadio.Infrastructure.Tts;

/// <summary>Client for the TTS sidecar (Plan.md §7.1).</summary>
public class HttpTtsEngine(HttpClient http) : ITtsEngine
{
    public const string DurationHeader = "X-Duration-Seconds";

    public async Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        var request = new SynthesizeRequest(markedUpText, options.VoiceId, options.Language, options.Rate, options.Engine);
        using var response = await http.PostAsJsonAsync("/synthesize", request, ct);
        response.EnsureSuccessStatusCode();

        var wavData = await response.Content.ReadAsByteArrayAsync(ct);

        double duration;
        if (response.Headers.TryGetValues(DurationHeader, out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var headerDuration))
        {
            duration = headerDuration;
        }
        else
        {
            duration = WavFile.GetDurationSeconds(wavData);
        }

        return new TtsResult(wavData, duration);
    }

    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
    {
        var voices = await http.GetFromJsonAsync<List<VoiceDto>>("/voices", ct) ?? [];
        return voices.Select(v => new TtsVoice(v.Id, v.Language, v.Gender)).ToList();
    }

    internal sealed record SynthesizeRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("rate")] double Rate,
        [property: JsonPropertyName("engine")] string Engine = "kokoro");

    internal sealed record VoiceDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("gender")] string Gender);
}
