using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tts;

/// <summary>
/// Books the first free voice booth for each synthesis. Hosts with an
/// ElevenLabs voice need an ElevenLabs booth (or the legacy station-settings
/// key); everyone else records in a local TTS booth (kokoro/piper).
/// </summary>
public class TtsEngineRouter(
    IHttpClientFactory httpClientFactory,
    StudioCoordinator coordinator,
    StudioProviderFactory factory,
    StationSettingsCache settingsCache) : ITtsEngine
{
    public const string ElevenLabsClientName = "tts-elevenlabs";

    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMinutes(10);

    public async Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        if (options.Engine == TtsEngines.ElevenLabs)
        {
            // Prefer a configured ElevenLabs booth; fall back to the legacy
            // station-settings key; finally degrade to a local default voice.
            var elBooth = await coordinator.GetFirstActiveAsync(
                StudioKind.VoiceBooth, StudioProviders.ElevenLabs, ct);
            if (elBooth is not null)
            {
                return await SynthesizeInBoothAsync(elBooth.Provider, markedUpText, options, ct);
            }

            var settings = await settingsCache.GetAsync(ct);
            if (settings.ElevenLabsEnabled && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
            {
                var elevenLabs = new ElevenLabsTtsEngine(
                    httpClientFactory.CreateClient(ElevenLabsClientName), settings.ElevenLabsApiKey);
                return await elevenLabs.SynthesizeAsync(markedUpText, options, ct);
            }

            options = options with { Engine = TtsEngines.Kokoro, VoiceId = "af_heart" };
        }

        return await SynthesizeInBoothAsync(StudioProviders.LocalTts, markedUpText, options, ct);
    }

    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
    {
        var booth = await coordinator.GetFirstActiveAsync(StudioKind.VoiceBooth, StudioProviders.LocalTts, ct);
        if (booth is null)
        {
            return [];
        }

        return await factory.CreateTtsEngine(booth).GetVoicesAsync(ct);
    }

    private async Task<TtsResult> SynthesizeInBoothAsync(
        string requiredProvider, string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + AcquireTimeout;
        Studio? booth = null;
        while (booth is null)
        {
            booth = await coordinator.TryAcquireAsync(
                StudioKind.VoiceBooth, requiredProvider, $"Voicing ({options.Engine}/{options.VoiceId})", ct);
            if (booth is not null)
            {
                break;
            }

            if (!await coordinator.AnyActiveAsync(StudioKind.VoiceBooth, requiredProvider, ct))
            {
                throw new InvalidOperationException($"No active voice booth for provider '{requiredProvider}'.");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("All voice booths busy for too long.");
            }

            await Task.Delay(AcquireRetryDelay, ct);
        }

        var success = false;
        try
        {
            var result = await factory.CreateTtsEngine(booth).SynthesizeAsync(markedUpText, options, ct);
            success = true;
            return result;
        }
        finally
        {
            await coordinator.ReleaseAsync(booth.Id, success, CancellationToken.None);
        }
    }
}
