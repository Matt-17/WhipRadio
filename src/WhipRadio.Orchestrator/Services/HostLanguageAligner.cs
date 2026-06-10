using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The station language is the main language: every host speaks it. Runs at
/// startup and whenever DefaultLanguage changes — hosts in another language are
/// switched over and get a voice that actually supports the new language
/// (e.g. German requires Piper; Kokoro is English-only).
/// </summary>
public class HostLanguageAligner(
    IDbContextFactory<RadioDbContext> dbFactory,
    VoiceCatalogService voices,
    ILogger<HostLanguageAligner> logger)
{
    public async Task AlignAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var language = StationLanguages.Normalize(settings?.DefaultLanguage);

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

            logger.LogInformation(
                "Aligned host {Name} to station language '{Language}' (engine {Engine}, voice {Voice})",
                host.Name, language, host.TtsEngine, host.VoiceId);
        }

        await db.SaveChangesAsync(ct);
    }
}
