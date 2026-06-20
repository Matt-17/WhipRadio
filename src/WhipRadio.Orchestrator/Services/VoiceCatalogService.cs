using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Assigns a TTS voice that matches a host's engine and gender.
/// ElevenLabs hosts get a freshly designed voice when the API allows it, else a
/// premade one.
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

            logger.LogWarning("ElevenLabs voice unavailable for {Name}; falling back to Kokoro", moderator.Name);
            moderator.TtsEngine = TtsEngines.Kokoro;
        }

        if (moderator.TtsEngine == TtsEngines.Piper)
        {
            return moderator.Gender == ModeratorGenders.Male ? "en_US-ryan-medium" : "en_US-lessac-medium";
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
        var description =
            $"A {genderWord} English radio host voice. Style: {moderator.Style}. {moderator.PersonaPrompt}";
        var previewText = "Hello and welcome to WhipRadio, great to have you with us tonight!";

        var created = await engine.TryCreateVoiceAsync(moderator.Name, description, previewText, ct);
        if (created is not null)
        {
            logger.LogInformation("Created ElevenLabs voice {VoiceId} for {Name}", created, moderator.Name);
            return created;
        }

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
