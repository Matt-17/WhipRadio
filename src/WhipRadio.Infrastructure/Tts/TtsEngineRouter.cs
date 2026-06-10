using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Tts;

/// <summary>
/// Routes synthesis by TtsVoiceOptions.Engine: "elevenlabs" goes to the cloud
/// (when enabled + key present), everything else to the local sidecar, which
/// hosts multiple engines itself (kokoro, piper).
/// </summary>
public class TtsEngineRouter(
    IHttpClientFactory httpClientFactory,
    HttpTtsEngine sidecarEngine,
    StationSettingsCache settingsCache) : ITtsEngine
{
    public const string ElevenLabsClientName = "tts-elevenlabs";

    public async Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        if (options.Engine == TtsEngines.ElevenLabs)
        {
            var settings = await settingsCache.GetAsync(ct);
            if (settings.ElevenLabsEnabled && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
            {
                var elevenLabs = new ElevenLabsTtsEngine(
                    httpClientFactory.CreateClient(ElevenLabsClientName), settings.ElevenLabsApiKey);
                return await elevenLabs.SynthesizeAsync(markedUpText, options, ct);
            }

            // Cloud disabled → degrade to the local default voice rather than going silent.
            options = options with { Engine = TtsEngines.Kokoro, VoiceId = "af_heart" };
        }

        return await sidecarEngine.SynthesizeAsync(markedUpText, options, ct);
    }

    public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
        => sidecarEngine.GetVoicesAsync(ct);
}
