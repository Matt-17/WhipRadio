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
/// Also feeds the host's day-memory so later talks can reference earlier ones.
/// </summary>
public class AnnouncementFactory(
    IScriptWriter scriptWriter,
    IVoiceDirector voiceDirector,
    ITtsEngine ttsEngine,
    MediaAnalysisRecorder analysisRecorder,
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    TimeProvider timeProvider,
    ILogger<AnnouncementFactory> logger)
{
    public async Task<Announcement> ProduceAsync(
        AnnouncementKind kind,
        Moderator moderator,
        Track? relatedTrack,
        string? facts,
        string stationName,
        CancellationToken ct,
        string? lengthHint = null)
    {
        bool allowBreath;
        await using (var settingsDb = await dbFactory.CreateDbContextAsync(ct))
        {
            allowBreath = (await settingsDb.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))
                ?.EnableBreathMarkers ?? false;
        }

        // Personal talks reference what the host already said today.
        if (kind == AnnouncementKind.PersonalNote && string.IsNullOrEmpty(facts))
        {
            facts = await GetTodaysMemoryAsync(moderator.Id, ct);
        }

        // Piper renders the bundled breath sample poorly — suppress regardless of setting.
        if (moderator.TtsEngine == TtsEngines.Piper)
        {
            allowBreath = false;
        }

        var request = new AnnouncementRequest(kind, stationName, moderator.Language, relatedTrack, facts, lengthHint);
        var script = await scriptWriter.WriteAsync(request, ct);
        var voiced = await voiceDirector.DirectAsync(script, moderator, ct);
        var normalized = SpeechMarkerNormalizer.Normalize(voiced, allowBreath);

        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(moderator.VoiceId, moderator.Language, moderator.SpeechRate, moderator.TtsEngine),
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

        // Remember talk topics (not track intros — those are throwaway).
        if (kind is AnnouncementKind.Banter or AnnouncementKind.PersonalNote or AnnouncementKind.Joke)
        {
            db.ModeratorMemories.Add(new ModeratorMemory
            {
                ModeratorId = moderator.Id,
                Date = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime),
                Content = script.Length > 300 ? script[..300] : script,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);

        // Mixer analysis (speech mode: loudness + silence, fast) — best effort.
        await analysisRecorder.AnalyzeAndStoreAsync(
            Core.Entities.PlayoutItemType.Announcement, id, relativePath, ct);

        logger.LogInformation(
            "Produced {Kind} announcement {Id} ({Duration:F1}s) by {Moderator} [{Engine}]",
            kind, id, tts.DurationSeconds, moderator.Name, moderator.TtsEngine);

        return announcement;
    }

    private async Task<string> GetTodaysMemoryAsync(int moderatorId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var memories = await db.ModeratorMemories.AsNoTracking()
            .Where(m => m.ModeratorId == moderatorId && m.Date == today)
            .OrderByDescending(m => m.CreatedAt)
            .Take(3)
            .Select(m => m.Content)
            .ToListAsync(ct);

        return memories.Count == 0 ? "nothing yet" : string.Join(" | ", memories);
    }
}
