using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Runs the full announcement pipeline:
/// ScriptWriter → VoiceDirector → SpeechMarkerNormalizer → TTS → WAV on disk → DB row.
/// </summary>
public class AnnouncementFactory(
    IScriptWriter scriptWriter,
    IVoiceDirector voiceDirector,
    ITtsEngine ttsEngine,
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    ILogger<AnnouncementFactory> logger)
{
    public async Task<Announcement> ProduceAsync(
        AnnouncementKind kind,
        Moderator moderator,
        Track? relatedTrack,
        string? facts,
        string stationName,
        CancellationToken ct)
    {
        var request = new AnnouncementRequest(kind, stationName, moderator.Language, relatedTrack, facts);
        var script = await scriptWriter.WriteAsync(request, ct);
        var voiced = await voiceDirector.DirectAsync(script, moderator, ct);
        var normalized = SpeechMarkerNormalizer.Normalize(voiced);

        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(moderator.VoiceId, moderator.Language, moderator.SpeechRate),
            ct);

        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "announcements", $"{id}.wav");
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, tts.WavData, ct);

        var announcement = new Announcement
        {
            Id = id,
            ModeratorId = moderator.Id,
            Kind = kind,
            ScriptText = script,
            VoicedText = normalized,
            FilePath = relativePath,
            DurationSeconds = tts.DurationSeconds,
            RelatedTrackId = relatedTrack?.Id,
            CreatedAt = DateTime.UtcNow,
            WasPlayed = false,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Announcements.Add(announcement);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Produced {Kind} announcement {Id} ({Duration:F1}s) by {Moderator}",
            kind, id, tts.DurationSeconds, moderator.Name);

        return announcement;
    }
}
