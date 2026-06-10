using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Assigns a TTS voice that matches a host's engine, gender and language.
/// German hosts get Piper (Kokoro has no German voices); ElevenLabs hosts get a
/// freshly designed voice when the API allows it, else a premade one.
/// </summary>
public class VoiceCatalogService(
    IHttpClientFactory httpClientFactory,
    StationSettingsCache settingsCache,
    ILogger<VoiceCatalogService> logger)
{
    private static readonly string[] KokoroMale = ["am_michael", "am_adam", "bm_george"];
    private static readonly string[] KokoroFemale = ["af_heart", "af_bella", "af_nicole", "bf_emma"];

    public async Task<string> PickVoiceAsync(Moderator moderator, CancellationToken ct)
    {
        if (moderator.TtsEngine == TtsEngines.ElevenLabs)
        {
            var voiceId = await TryElevenLabsVoiceAsync(moderator, ct);
            if (voiceId is not null)
            {
                return voiceId;
            }

            logger.LogWarning("ElevenLabs voice unavailable for {Name}; falling back to local engine", moderator.Name);
            moderator.TtsEngine = moderator.Language.StartsWith("de") ? TtsEngines.Piper : TtsEngines.Kokoro;
        }

        // German requires Piper — Kokoro is English-only, so the engine is corrected.
        if (moderator.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            moderator.TtsEngine = TtsEngines.Piper;
        }

        if (moderator.TtsEngine == TtsEngines.Piper)
        {
            return moderator.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? (moderator.Gender == ModeratorGenders.Male ? "de_DE-thorsten-medium" : "de_DE-eva_k-x_low")
                : (moderator.Gender == ModeratorGenders.Male ? "en_US-ryan-medium" : "en_US-lessac-medium");
        }

        var pool = moderator.Gender == ModeratorGenders.Male ? KokoroMale : KokoroFemale;
        return pool[Random.Shared.Next(pool.Length)];
    }

    private async Task<string?> TryElevenLabsVoiceAsync(Moderator moderator, CancellationToken ct)
    {
        var settings = await settingsCache.GetAsync(ct);
        if (!settings.ElevenLabsEnabled || string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
        {
            return null;
        }

        var engine = new ElevenLabsTtsEngine(
            httpClientFactory.CreateClient(TtsEngineRouter.ElevenLabsClientName), settings.ElevenLabsApiKey);

        var genderWord = moderator.Gender == ModeratorGenders.Male ? "male" : "female";
        var languageWord = moderator.Language.StartsWith("de") ? "German" : "English";
        var description =
            $"A {genderWord} {languageWord} radio host voice. Style: {moderator.Style}. {moderator.PersonaPrompt}";
        var previewText = moderator.Language.StartsWith("de")
            ? "Hallo und herzlich willkommen bei WhipRadio, schön dass ihr eingeschaltet habt!"
            : "Hello and welcome to WhipRadio, great to have you with us tonight!";

        var created = await engine.TryCreateVoiceAsync(moderator.Name, description, previewText, ct);
        if (created is not null)
        {
            logger.LogInformation("Created ElevenLabs voice {VoiceId} for {Name}", created, moderator.Name);
            return created;
        }

        // Voice design unavailable (plan/API): pick a premade voice by gender.
        try
        {
            var voices = await engine.GetVoicesAsync(ct);
            var match = voices.FirstOrDefault(v => v.Gender == moderator.Gender) ?? voices.FirstOrDefault();
            return match?.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ElevenLabs voice list failed");
            return null;
        }
    }
}
