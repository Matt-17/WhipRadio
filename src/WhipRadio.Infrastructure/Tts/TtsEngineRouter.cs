using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tts;

/// <summary>
/// Books the first free voice booth for each synthesis. Hosts with an
/// ElevenLabs voice need an ElevenLabs booth (or the legacy station-settings
/// key); everyone else records in the local Qwen booth.
/// </summary>
public class TtsEngineRouter(
    IHttpClientFactory httpClientFactory,
    StudioCoordinator coordinator,
    StudioProviderFactory factory,
    StationSettingsCache settingsCache,
    StudioHistoryRecorder history) : ITtsEngine
{
    public const string ElevenLabsClientName = "tts-elevenlabs";

    public async Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
    {
        if (options.Engine == TtsEngines.ElevenLabs)
        {
            // Prefer a configured ElevenLabs booth; fall back to the legacy
            // station-settings key; finally degrade to the local Qwen booth.
            var elBooth = await coordinator.GetFirstActiveAsync(
                StudioKind.VoiceBooth, StudioProviders.ElevenLabs, ct);
            if (elBooth is not null)
            {
                return await SynthesizeInBoothAsync(elBooth.Provider, markedUpText, options, ct);
            }

            var settings = await settingsCache.GetAsync(ct);
            if (settings.ElevenLabsEnabled && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
            {
                return await SynthesizeExternalAsync(
                    "Voice Booth (ElevenLabs settings)",
                    StudioProviders.ElevenLabs,
                    markedUpText,
                    options,
                    async token =>
                    {
                        var elevenLabs = new ElevenLabsTtsEngine(
                            httpClientFactory.CreateClient(ElevenLabsClientName), settings.ElevenLabsApiKey);
                        return await elevenLabs.SynthesizeAsync(markedUpText, options, token);
                    },
                    ct);
            }

            // The Qwen booth resolves a missing handle to an existing designed voice.
            options = options with { Engine = TtsEngines.Qwen, VoiceId = "" };
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
        var label = $"Voicing ({options.Engine}/{options.VoiceId})";

        // Order against everyone else waiting on the shared GPU (priority -> affinity -> FIFO);
        // local-TTS booths only reload the model when switching away from text/music work.
        var lease = await coordinator.AcquireForGpuJobAsync(StudioKind.VoiceBooth, requiredProvider, label, ct);
        if (lease is null)
        {
            throw new InvalidOperationException($"No reachable voice booth for provider '{requiredProvider}'.");
        }

        var booth = lease.Studio;
        var success = false;
        Guid? historyId = null;
        try
        {
            historyId = await history.BeginAsync(
                booth, label, VoicePrompt(markedUpText), VoiceDetail(options), ct);
            var result = await factory.CreateTtsEngine(booth).SynthesizeAsync(markedUpText, options, ct);
            await history.CompleteAsync(historyId, VoiceResultDetail(result), null, CancellationToken.None);
            success = true;
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await history.FailAsync(historyId, ex, null, CancellationToken.None);
            throw;
        }
        finally
        {
            await lease.CompleteAsync(success, CancellationToken.None);
        }
    }

    private async Task<TtsResult> SynthesizeExternalAsync(
        string studioName,
        string provider,
        string markedUpText,
        TtsVoiceOptions options,
        Func<CancellationToken, Task<TtsResult>> synthesize,
        CancellationToken ct)
    {
        var historyId = await history.BeginAsync(
            studioId: null,
            studioName,
            StudioKind.VoiceBooth,
            provider,
            $"Voicing ({options.Engine}/{options.VoiceId})",
            VoicePrompt(markedUpText),
            VoiceDetail(options),
            ct);

        try
        {
            var result = await synthesize(ct);
            await history.CompleteAsync(historyId, VoiceResultDetail(result), null, CancellationToken.None);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await history.FailAsync(historyId, ex, null, CancellationToken.None);
            throw;
        }
    }

    private static string VoicePrompt(string markedUpText)
        => markedUpText;

    private static string VoiceDetail(TtsVoiceOptions options)
    {
        var lines = new List<string>
        {
            $"Engine: {options.Engine}",
            $"Voice: {options.VoiceId}",
            $"Language: {options.Language}",
            $"Rate: {options.Rate:0.###}",
        };

        if (!string.IsNullOrWhiteSpace(options.Instruction))
        {
            lines.Add($"Instruction: {options.Instruction}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string VoiceResultDetail(TtsResult result)
        => $"Duration: {result.DurationSeconds:0.###}s{Environment.NewLine}Audio bytes: {result.WavData.Length}";
}
