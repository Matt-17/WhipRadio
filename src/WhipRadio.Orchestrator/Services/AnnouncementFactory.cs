using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
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
    IPromptContextBuilder promptContextBuilder,
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
        string? lengthHint = null,
        string? alreadySpokenContext = null)
    {
        var allowBreath = await GetAllowBreathAsync(moderator, ct);

        // Personal talks reference what the host already said today.
        if (kind == AnnouncementKind.PersonalNote && string.IsNullOrEmpty(facts))
        {
            facts = await GetTodaysMemoryAsync(moderator.Id, ct);
        }

        var scriptContext = await promptContextBuilder.BuildAsync(
            new PromptContextInput(
                PromptScope.AnnouncementScript,
                Moderator: moderator,
                AnnouncementKind: kind,
                RelatedTrack: relatedTrack,
                Facts: facts,
                LengthHint: lengthHint,
                Purpose: kind.ToString(),
                AlreadySpokenContext: alreadySpokenContext),
            ct);

        var request = new AnnouncementRequest(
            kind,
            stationName,
            moderator.Language,
            relatedTrack,
            facts,
            lengthHint,
            scriptContext);
        var script = await scriptWriter.WriteAsync(request, ct);

        var voiceContext = await promptContextBuilder.BuildAsync(
            new PromptContextInput(
                PromptScope.VoiceDirection,
                Moderator: moderator,
                AnnouncementKind: kind,
                RelatedTrack: relatedTrack,
                Facts: facts,
                LengthHint: lengthHint,
                Purpose: kind.ToString(),
                AlreadySpokenContext: alreadySpokenContext),
            ct);

        var voiced = await voiceDirector.DirectAsync(script, moderator, ct, voiceContext);
        var normalized = SpeechMarkerNormalizer.Normalize(voiced, allowBreath);

        // Qwen takes a natural-language delivery instruction (style from the
        // host persona; breath cue follows the station flag). Other engines
        // ignore it — markers remain the portable baseline for hard timing.
        var instruction = BuildTtsInstruction(moderator, allowBreath);

        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(
                moderator.VoiceId, moderator.Language, moderator.SpeechRate, moderator.TtsEngine, instruction),
            ct);
        var producedWords = PromptWordBudget.CountWords(script);
        logger.LogInformation(
            "Announcement prompt budget for {Kind}: available {AvailableSeconds}s, target {TargetWords} words, produced {ProducedWords} words, rendered {Duration:F1}s",
            kind,
            scriptContext.AvailableSeconds,
            scriptContext.WordBudget,
            producedWords,
            tts.DurationSeconds);

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
        db.TalkBreaks.Add(CreateTalkBreak(announcement, kind, moderator, relatedTrack, scriptContext));

        // Remember talk topics (not track intros — those are throwaway).
        if (kind is AnnouncementKind.Banter or AnnouncementKind.PersonalNote or AnnouncementKind.Joke)
        {
            db.ModeratorMemories.Add(new ModeratorMemory
            {
                ModeratorId = moderator.Id,
                Layer = ModeratorMemoryLayer.DayMemory,
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

    public async Task<Announcement> ProduceDirectAsync(
        AnnouncementKind kind,
        TalkPartKind partKind,
        TalkBreakPriority priority,
        Moderator moderator,
        string text,
        string purpose,
        CancellationToken ct,
        string title = "Announcement",
        DateTime? expiresAtUtc = null,
        Track? relatedTrack = null,
        Guid? talkBitId = null,
        int? desiredDurationSeconds = null,
        int? wordBudget = null)
    {
        var script = text.Trim();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Direct announcement text cannot be empty.", nameof(text));
        }

        var allowBreath = await GetAllowBreathAsync(moderator, ct);
        var normalized = SpeechMarkerNormalizer.Normalize(script, allowBreath);
        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(
                moderator.VoiceId,
                moderator.Language,
                moderator.SpeechRate,
                moderator.TtsEngine,
                BuildTtsInstruction(moderator, allowBreath)),
            ct);

        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "announcements", $"{id}.wav");
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, tts.WavData, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
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
            CreatedAt = now,
            WasPlayed = false,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Announcements.Add(announcement);
        db.TalkBreaks.Add(CreateTalkBreak(
            announcement,
            moderator,
            relatedTrack,
            priority,
            partKind,
            purpose,
            title,
            now,
            expiresAtUtc,
            desiredDurationSeconds,
            wordBudget,
            talkBitId));
        await db.SaveChangesAsync(ct);

        await analysisRecorder.AnalyzeAndStoreAsync(
            Core.Entities.PlayoutItemType.Announcement, id, relativePath, ct);

        logger.LogInformation(
            "Produced direct {Kind} announcement {Id} ({Duration:F1}s) by {Moderator} [{Engine}]",
            kind, id, tts.DurationSeconds, moderator.Name, moderator.TtsEngine);

        return announcement;
    }

    private async Task<string> GetTodaysMemoryAsync(int moderatorId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var memories = await db.ModeratorMemories.AsNoTracking()
            .Where(m => m.ModeratorId == moderatorId
                && m.Layer == ModeratorMemoryLayer.DayMemory
                && m.Date == today)
            .OrderByDescending(m => m.CreatedAt)
            .Take(3)
            .Select(m => m.Content)
            .ToListAsync(ct);

        return memories.Count == 0 ? "nothing yet" : string.Join(" | ", memories);
    }

    private async Task<bool> GetAllowBreathAsync(Moderator moderator, CancellationToken ct)
    {
        await using var settingsDb = await dbFactory.CreateDbContextAsync(ct);
        var allowBreath = (await settingsDb.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct))
            .EnableBreathMarkers;

        // Piper renders the bundled breath sample poorly; suppress regardless of setting.
        return moderator.TtsEngine == TtsEngines.Piper ? false : allowBreath;
    }

    private static string? BuildTtsInstruction(Moderator moderator, bool allowBreath)
        => moderator.TtsEngine == TtsEngines.Qwen
            ? $"Radio host, {moderator.Style} delivery."
                + (allowBreath ? " Natural audible breaths between sentences." : "")
            : null;

    private static TalkBreak CreateTalkBreak(
        Announcement announcement,
        AnnouncementKind kind,
        Moderator moderator,
        Track? relatedTrack,
        Core.Prompting.PromptContext context)
    {
        var now = DateTime.UtcNow;
        var priority = PriorityFor(kind);
        var expiresAt = ExpiresAtFor(kind, now);
        return CreateTalkBreak(
            announcement,
            moderator,
            relatedTrack,
            priority,
            PartKindFor(kind),
            PurposeFor(kind),
            "Announcement",
            now,
            expiresAt,
            context.AvailableSeconds,
            context.WordBudget);
    }

    private static TalkBreak CreateTalkBreak(
        Announcement announcement,
        Moderator moderator,
        Track? relatedTrack,
        TalkBreakPriority priority,
        TalkPartKind partKind,
        string purpose,
        string title,
        DateTime now,
        DateTime? expiresAt,
        int? desiredDurationSeconds,
        int? wordBudget,
        Guid? talkBitId = null)
    {
        return new TalkBreak
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcement.Id,
            ModeratorId = moderator.Id,
            Priority = priority,
            Status = TalkBreakStatus.Rendered,
            Purpose = purpose,
            Title = title,
            CreatedAtUtc = now,
            RenderedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            DurationSeconds = announcement.DurationSeconds,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = partKind,
                    Status = TalkPartStatus.Rendered,
                    Priority = priority,
                    Purpose = purpose,
                    AnnouncementId = announcement.Id,
                    RelatedTrackId = relatedTrack?.Id,
                    TalkBitId = talkBitId,
                    DesiredDurationSeconds = desiredDurationSeconds,
                    WordBudget = wordBudget,
                    ExpiresAtUtc = expiresAt,
                    CreatedAtUtc = now,
                },
            ],
        };
    }

    private static TalkBreakPriority PriorityFor(AnnouncementKind kind)
        => kind switch
        {
            AnnouncementKind.ListenerGreeting or AnnouncementKind.RequestDedication => TalkBreakPriority.High,
            AnnouncementKind.EmergencyMessage => TalkBreakPriority.Emergency,
            AnnouncementKind.Weather or AnnouncementKind.News => TalkBreakPriority.Scheduled,
            AnnouncementKind.StationId => TalkBreakPriority.Low,
            _ => TalkBreakPriority.Normal,
        };

    private static DateTime? ExpiresAtFor(AnnouncementKind kind, DateTime now)
        => kind switch
        {
            AnnouncementKind.Weather => now.AddMinutes(30),
            AnnouncementKind.News => now.AddHours(1),
            AnnouncementKind.SongIntro or AnnouncementKind.SongOutro or AnnouncementKind.HostChange => now.AddHours(2),
            AnnouncementKind.EmergencyMessage => now.AddHours(1),
            AnnouncementKind.ListenerGreeting or AnnouncementKind.RequestDedication => now.AddHours(24),
            _ => null,
        };

    private static string PurposeFor(AnnouncementKind kind)
        => kind switch
        {
            AnnouncementKind.Weather => "WeatherReport",
            AnnouncementKind.News => "NewsReport",
            _ => kind.ToString(),
        };

    private static TalkPartKind PartKindFor(AnnouncementKind kind)
        => kind switch
        {
            AnnouncementKind.SongIntro => TalkPartKind.NextSongIntro,
            AnnouncementKind.SongOutro => TalkPartKind.PreviousSongComment,
            AnnouncementKind.Weather => TalkPartKind.Weather,
            AnnouncementKind.News => TalkPartKind.News,
            AnnouncementKind.ListenerGreeting => TalkPartKind.ListenerGreeting,
            AnnouncementKind.RequestDedication => TalkPartKind.RequestDedication,
            AnnouncementKind.Banter => TalkPartKind.Banter,
            AnnouncementKind.PersonalNote => TalkPartKind.PersonalNote,
            AnnouncementKind.Joke => TalkPartKind.Joke,
            AnnouncementKind.TalkBit => TalkPartKind.TalkBit,
            AnnouncementKind.Jingle => TalkPartKind.Jingle,
            AnnouncementKind.EmergencyMessage => TalkPartKind.EmergencyMessage,
            AnnouncementKind.StationId => TalkPartKind.StationId,
            AnnouncementKind.HostChange => TalkPartKind.HostChange,
            _ => TalkPartKind.Banter,
        };
}
