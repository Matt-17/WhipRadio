using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The station language is the main language: every host speaks it. Runs at
/// startup and whenever DefaultLanguage changes — hosts in another language are
/// switched over, get a voice that supports the new language (German requires
/// Piper; Kokoro is English-only) and their persona prompt is translated too:
/// a German persona drags the LLM into German output regardless of instructions.
/// </summary>
public class HostLanguageAligner(
    IDbContextFactory<RadioDbContext> dbFactory,
    VoiceCatalogService voices,
    IServiceScopeFactory scopeFactory,
    ILogger<HostLanguageAligner> logger)
{
    public async Task AlignAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var language = StationLanguages.Normalize(settings.DefaultLanguage);

        var offLanguageHosts = await db.Moderators
            .Where(m => m.Language != language)
            .ToListAsync(ct);
        if (offLanguageHosts.Count == 0)
        {
            return;
        }

        foreach (var host in offLanguageHosts)
        {
            host.Language = language;

            // ElevenLabs voices are multilingual; local engines are not.
            if (host.TtsEngine != TtsEngines.ElevenLabs)
            {
                host.TtsEngine = language == "de" ? TtsEngines.Piper : host.TtsEngine;
                host.VoiceId = await voices.PickVoiceAsync(host, ct);
            }

            host.PersonaPrompt = await TranslatePersonaAsync(host.PersonaPrompt, language, ct);

            logger.LogInformation(
                "Aligned host {Name} to station language '{Language}' (engine {Engine}, voice {Voice})",
                host.Name, language, host.TtsEngine, host.VoiceId);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<string> TranslatePersonaAsync(string persona, string language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(persona))
        {
            return persona;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();
            var promptContextBuilder = scope.ServiceProvider.GetRequiredService<IPromptContextBuilder>();
            var promptContext = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.Utility,
                    Facts: $"Target language: {language}",
                    Purpose: "Translate host persona to station language"),
                ct);

            var languageName = language == "de" ? "German" : "English";
            var translated = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(
                $"You translate radio host persona descriptions to {languageName}. " +
                "Keep the character, tone and second-person form. Output ONLY the translated persona.\n\n" +
                promptContext.RenderSituation(),
                persona, ct));
            return string.IsNullOrWhiteSpace(translated) ? persona : translated;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Persona translation failed; keeping the original text");
            return persona;
        }
    }
}
