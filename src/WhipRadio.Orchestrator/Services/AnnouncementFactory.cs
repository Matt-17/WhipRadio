using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Runs the full announcement pipeline:
/// AnnouncementWriter (one LLM run: script + delivery + voice) → SpeechMarkerNormalizer →
/// TTS → WAV on disk → DB row. Also feeds the host's day-memory so later talks can
/// reference earlier ones.
/// </summary>
public class AnnouncementFactory(
    IAnnouncementWriter announcementWriter,
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
        string? alreadySpokenContext = null,
        DateTimeOffset? localNowOverride = null,
        string? purpose = null)
    {
        using var priorityScope = PriorityScope(kind);
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
                Purpose: purpose ?? kind.ToString(),
                AlreadySpokenContext: alreadySpokenContext,
                LocalNowOverride: localNowOverride),
            ct);

        var request = new AnnouncementRequest(
            kind,
            stationName,
            moderator.Language,
            relatedTrack,
            facts,
            lengthHint,
            scriptContext);
        var spoken = await announcementWriter.WriteAsync(request, moderator, ct);
        var script = spoken.Script;
        var normalized = SpeechMarkerNormalizer.Normalize(spoken.Delivery, allowBreath);

        // Qwen takes a natural-language delivery instruction (style from the host
        // persona, shaded by the per-delivery hint; breath cue follows the station
        // flag). Other engines ignore it — markers remain the portable baseline.
        var instruction = BuildTtsInstruction(moderator, allowBreath, spoken.DeliveryPrompt);

        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(
                moderator.VoiceId, moderator.Language, ResolveRate(spoken.Rate, moderator.SpeechRate), moderator.TtsEngine, instruction,
                Operation: ScriptOperationLabels.Describe(kind, purpose), SpeakerName: moderator.Name),
            ct);
        var producedWords = PromptWordBudget.CountWords(script);
        if (tts.DurationSeconds <= 0)
        {
            logger.LogWarning(
                "TTS produced zero-duration audio for {Kind} announcement by {Moderator} [{Engine}]. "
                + "This will cause the radio mixer to skip the item.",
                kind, moderator.Name, moderator.TtsEngine);
        }

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

    public async Task<AnnouncementScriptDraft> WriteScriptDraftAsync(
        AnnouncementKind kind,
        Moderator moderator,
        Track? relatedTrack,
        string? facts,
        string stationName,
        CancellationToken ct,
        string? lengthHint = null,
        string? alreadySpokenContext = null,
        DateTimeOffset? localNowOverride = null,
        PromptPriority priority = PromptPriority.Normal,
        string? purpose = null)
    {
        using var priorityScope = PriorityScope(kind);

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
                Purpose: purpose ?? kind.ToString(),
                Priority: priority,
                AlreadySpokenContext: alreadySpokenContext,
                LocalNowOverride: localNowOverride),
            ct);

        var request = new AnnouncementRequest(
            kind,
            stationName,
            moderator.Language,
            relatedTrack,
            facts,
            lengthHint,
            scriptContext);
        var spoken = await announcementWriter.WriteAsync(request, moderator, ct);

        return new AnnouncementScriptDraft(
            kind,
            moderator,
            relatedTrack,
            facts,
            lengthHint,
            alreadySpokenContext,
            localNowOverride,
            spoken.Script,
            spoken.Delivery,
            spoken.DeliveryPrompt,
            spoken.Rate,
            scriptContext);
    }

    public async Task<Announcement> ProduceFromDraftAsync(AnnouncementScriptDraft draft, CancellationToken ct)
    {
        using var priorityScope = PriorityScope(draft.Kind);
        var allowBreath = await GetAllowBreathAsync(draft.Moderator, ct);

        // The combined run already produced the delivery + voice direction; finalizing a
        // draft is TTS only — no second LLM call.
        var normalized = SpeechMarkerNormalizer.Normalize(draft.Delivery, allowBreath);
        var instruction = BuildTtsInstruction(draft.Moderator, allowBreath, draft.DeliveryPrompt);

        var tts = await ttsEngine.SynthesizeAsync(
            normalized,
            new TtsVoiceOptions(
                draft.Moderator.VoiceId,
                draft.Moderator.Language,
                ResolveRate(draft.Rate, draft.Moderator.SpeechRate),
                draft.Moderator.TtsEngine,
                instruction,
                Operation: ScriptOperationLabels.Describe(draft.Kind, draft.ScriptContext.Purpose),
                SpeakerName: draft.Moderator.Name),
            ct);
        var producedWords = PromptWordBudget.CountWords(draft.Script);
        if (tts.DurationSeconds <= 0)
        {
            logger.LogWarning(
                "TTS produced zero-duration audio for {Kind} announcement (from draft) by {Moderator} [{Engine}]. "
                + "This will cause the radio mixer to skip the item.",
                draft.Kind, draft.Moderator.Name, draft.Moderator.TtsEngine);
        }

        logger.LogInformation(
            "Announcement prompt budget for {Kind}: available {AvailableSeconds}s, target {TargetWords} words, produced {ProducedWords} words, rendered {Duration:F1}s",
            draft.Kind,
            draft.ScriptContext.AvailableSeconds,
            draft.ScriptContext.WordBudget,
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
            ModeratorId = draft.Moderator.Id,
            Kind = draft.Kind,
            ScriptText = draft.Script,
            VoicedText = normalized,
            FilePath = relativePath,
            DurationSeconds = tts.DurationSeconds,
            RelatedTrackId = draft.RelatedTrack?.Id,
            CreatedAt = DateTime.UtcNow,
            WasPlayed = false,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Announcements.Add(announcement);
        db.TalkBreaks.Add(CreateTalkBreak(
            announcement,
            draft.Kind,
            draft.Moderator,
            draft.RelatedTrack,
            draft.ScriptContext));

        if (draft.Kind is AnnouncementKind.Banter or AnnouncementKind.PersonalNote or AnnouncementKind.Joke)
        {
            db.ModeratorMemories.Add(new ModeratorMemory
            {
                ModeratorId = draft.Moderator.Id,
                Layer = ModeratorMemoryLayer.DayMemory,
                Date = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime),
                Content = draft.Script.Length > 300 ? draft.Script[..300] : draft.Script,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);

        await analysisRecorder.AnalyzeAndStoreAsync(
            Core.Entities.PlayoutItemType.Announcement, id, relativePath, ct);

        logger.LogInformation(
            "Produced {Kind} announcement {Id} ({Duration:F1}s) by {Moderator} [{Engine}]",
            draft.Kind, id, tts.DurationSeconds, draft.Moderator.Name, draft.Moderator.TtsEngine);

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
        using var priorityScope = PriorityScope(kind);
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
                BuildTtsInstruction(moderator, allowBreath, null),
                Operation: ScriptOperationLabels.Describe(kind, purpose),
                SpeakerName: moderator.Name),
            ct);

        if (tts.DurationSeconds <= 0)
        {
            logger.LogWarning(
                "TTS produced zero-duration audio for direct {Kind} announcement by {Moderator} [{Engine}]. "
                + "This will cause the radio mixer to skip the item.",
                kind, moderator.Name, moderator.TtsEngine);
        }

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

    private static string? BuildTtsInstruction(Moderator moderator, bool allowBreath, string? deliveryPrompt)
        => moderator.TtsEngine == TtsEngines.Qwen
            ? $"Radio host, {moderator.Style} delivery."
                + (string.IsNullOrWhiteSpace(deliveryPrompt) ? "" : $" {deliveryPrompt.Trim()}")
                + (allowBreath ? " Natural audible breaths between sentences." : "")
            : null;

    /// <summary>Per-delivery rate from the model wins when present (clamped to a sane band);
    /// otherwise the moderator's configured rate.</summary>
    private static double ResolveRate(double? llmRate, double moderatorRate)
        => llmRate is { } rate && rate > 0 ? Math.Clamp(rate, 0.7, 1.3) : moderatorRate;

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

    /// <summary>Default GPU scheduling priority for a standalone announcement; defers to any
    /// production-wide scope already set by the caller (e.g. a news package's air-time ramp).</summary>
    private static IDisposable PriorityScope(AnnouncementKind kind)
        => GpuPriorityContext.PushIfUnset(GpuPriorityFor(kind));

    private static int GpuPriorityFor(AnnouncementKind kind)
        => kind switch
        {
            AnnouncementKind.EmergencyMessage => GpuJobPriority.Emergency,
            AnnouncementKind.SongIntro or AnnouncementKind.SongOutro
                or AnnouncementKind.ListenerGreeting or AnnouncementKind.RequestDedication
                or AnnouncementKind.HostChange => GpuJobPriority.High,
            AnnouncementKind.StationId or AnnouncementKind.Jingle => GpuJobPriority.Low,
            _ => GpuJobPriority.Normal,
        };

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

    public sealed record AnnouncementScriptDraft(
        AnnouncementKind Kind,
        Moderator Moderator,
        Track? RelatedTrack,
        string? Facts,
        string? LengthHint,
        string? AlreadySpokenContext,
        DateTimeOffset? LocalNowOverride,
        string Script,
        string Delivery,
        string? DeliveryPrompt,
        double? Rate,
        Core.Prompting.PromptContext ScriptContext);
}
