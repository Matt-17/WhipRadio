using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Produce-ahead engine for multi-speaker conversations (Phase 3c.2): ensures a
/// Planned episode exists for each upcoming podcast-show slot, writes the whole
/// script in one LLM call, voices every turn with the speaker's own designed
/// voice, and assembles one WAV wrapped in a ScheduledOnly announcement.
/// Resume is status-level by design: a restart mid-voicing leaves the segment
/// Scripted (the expensive LLM half is persisted in TurnsJson) and simply
/// re-voices — no per-turn checkpoints.
/// </summary>
public sealed class ConversationProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ArtistMemberVoiceQueue memberVoiceQueue,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    IStationMetrics metrics,
    IOptions<RadioOptions> radioOptions,
    ILogger<ConversationProductionService> logger) : BackgroundService
{
    public const string ConversationsDirectory = "conversations";

    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProductionBudget = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan EpisodePrepareAhead = TimeSpan.FromMinutes(90);
    private const int RecentEpisodeTitleCount = 8;
    private const int MaxDeepBackgroundChars = 500;

    private readonly ConcurrentDictionary<Guid, byte> _producing = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            const string kind = "conversation";
            var cycleStart = Stopwatch.GetTimestamp();
            try
            {
                await RunCycleAsync(stoppingToken);
                metrics.GenerationSucceeded(kind, Stopwatch.GetElapsedTime(cycleStart));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.GenerationFailed(kind);
                logger.LogError(ex, "Conversation production cycle failed");
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    internal Task RunCycleForTestsAsync(CancellationToken ct) => RunCycleAsync(ct);

    private async Task RunCycleAsync(CancellationToken ct)
    {
        await EnsureShowEpisodesAsync(ct);

        Guid segmentId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await FailMissedEpisodesAsync(db, ct);

            var producingIds = _producing.Keys.ToList();
            var next = await db.ConversationSegments.AsNoTracking()
                .Where(segment => (segment.Status == ConversationStatus.Planned
                        || segment.Status == ConversationStatus.Scripted)
                    && !producingIds.Contains(segment.Id))
                .OrderBy(segment => segment.TargetUtc == null) // scheduled episodes first
                .ThenBy(segment => segment.TargetUtc)
                .ThenBy(segment => segment.CreatedAtUtc)
                .Select(segment => (Guid?)segment.Id)
                .FirstOrDefaultAsync(ct) ?? Guid.Empty;
            if (next == Guid.Empty)
            {
                return;
            }

            segmentId = next;
        }

        if (!_producing.TryAdd(segmentId, 0))
        {
            return;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ProductionBudget);
            await ProduceAsync(segmentId, ct, budget.Token);
        }
        finally
        {
            _producing.TryRemove(segmentId, out _);
        }
    }

    /// <summary>Creates a Planned episode for every upcoming podcast slot occurrence
    /// inside the prepare-ahead window that has no segment yet.</summary>
    private async Task EnsureShowEpisodesAsync(CancellationToken ct)
    {
        var localNow = timeProvider.GetLocalNow();
        var created = false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var show in await db.PodcastShows.Where(show => show.IsEnabled).ToListAsync(ct))
        {
            var occurrence = PodcastShowScheduler.NextOccurrence(localNow, show.DayOfWeek, show.StartMinute);
            if (occurrence - localNow > EpisodePrepareAhead)
            {
                continue;
            }

            var targetUtc = occurrence.UtcDateTime;
            if (await db.ConversationSegments.AnyAsync(
                segment => segment.PodcastShowId == show.Id && segment.TargetUtc == targetUtc, ct))
            {
                continue;
            }

            db.ConversationSegments.Add(new ConversationSegment
            {
                Id = Guid.NewGuid(),
                Kind = ConversationKind.Podcast,
                Structure = ConversationStructure.Freeform,
                Topic = show.Name,
                Brief = show.Brief,
                TargetDurationMinutes = PodcastShowScheduler.NormalizeEpisodeMinutes(show.EpisodeMinutes),
                ParticipantsJson = show.ParticipantsJson,
                PodcastShowId = show.Id,
                TargetUtc = targetUtc,
                Status = ConversationStatus.Planned,
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            });
            created = true;
            logger.LogInformation(
                "Podcast episode planned for \"{Show}\" at {Target:u}", show.Name, targetUtc);
        }

        if (created)
        {
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishConversationsChangedAsync(ct);
        }
    }

    /// <summary>An episode whose slot passed the late window can never air — fail it cleanly.</summary>
    private async Task FailMissedEpisodesAsync(RadioDbContext db, CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(
            -TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
        var missed = await db.ConversationSegments
            .Where(segment => segment.TargetUtc != null
                && segment.TargetUtc < cutoff
                && (segment.Status == ConversationStatus.Planned
                    || segment.Status == ConversationStatus.Scripted))
            .ToListAsync(ct);
        if (missed.Count == 0)
        {
            return;
        }

        foreach (var segment in missed)
        {
            segment.Status = ConversationStatus.Failed;
            segment.FailureReason = "Production did not finish before the episode's slot.";
            segment.ProductionState = null;
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishConversationsChangedAsync(ct);
    }

    private async Task ProduceAsync(Guid segmentId, CancellationToken stoppingToken, CancellationToken ct)
    {
        ConversationSegment segment;
        StationSettings settings;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            segment = await db.ConversationSegments.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == segmentId, ct)
                ?? throw new KeyNotFoundException("Conversation segment was not found.");
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        }

        var targetUtc = segment.TargetUtc;
        using var priority = GpuPriorityContext.Push(targetUtc is { } airtime
            ? () => NewsAirtimeRamp.Priority(airtime, timeProvider.GetUtcNow().UtcDateTime)
            : () => GpuJobPriority.Low);

        var step = "resolving speakers";
        try
        {
            var participants = Deserialize<ConversationParticipant>(segment.ParticipantsJson);
            if (participants.Count < 2)
            {
                await MarkFailedAsync(segment.Id, "A conversation needs at least two participants.", ct);
                return;
            }

            var speakers = await ResolveSpeakersAsync(segment.Id, participants, settings, ct);
            if (speakers is null)
            {
                return; // waiting for a designed voice — retried next cycle
            }

            using var scope = scopeFactory.CreateScope();

            if (segment.Status == ConversationStatus.Planned)
            {
                step = "writing the script";
                await UpdateStateAsync(segment.Id, "Writing the script.", 1, 1, ct);
                var script = await WriteScriptAsync(scope.ServiceProvider, segment, settings, speakers, ct);
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var tracked = await db.ConversationSegments.FirstAsync(s => s.Id == segment.Id, ct);
                tracked.TurnsJson = JsonSerializer.Serialize(script.Turns);
                tracked.Transcript = script.Transcript;
                tracked.Title = script.Title;
                tracked.Status = ConversationStatus.Scripted;
                tracked.ProductionState = "Script ready.";
                await db.SaveChangesAsync(ct);
                await productionUpdates.PublishConversationsChangedAsync(ct);
                segment = tracked;
            }

            step = "voicing turns";
            var turns = Deserialize<ConversationTurn>(segment.TurnsJson ?? "[]");
            if (turns.Count == 0)
            {
                await MarkFailedAsync(segment.Id, "The scripted segment has no turns.", ct);
                return;
            }

            var voiceBySpeaker = speakers.Voices;
            var tts = scope.ServiceProvider.GetRequiredService<ITtsEngine>();
            var recorded = new List<ConversationTurnAudio>(turns.Count);
            var stepTotal = turns.Count + 1;
            for (var i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                if (!voiceBySpeaker.TryGetValue(turn.SpeakerKey, out var voice))
                {
                    await MarkFailedAsync(segment.Id, $"Turn speaker \"{turn.SpeakerKey}\" is not in the roster.", ct);
                    return;
                }

                await UpdateStateAsync(
                    segment.Id, $"Recording {voice.DisplayName} ({i + 1}/{turns.Count}).", i + 1, stepTotal, ct);
                var result = await tts.SynthesizeAsync(
                    string.IsNullOrWhiteSpace(turn.Markers) ? turn.Text : turn.Markers,
                    new TtsVoiceOptions(
                        voice.VoiceId,
                        voice.Language,
                        voice.Rate,
                        voice.Engine,
                        voice.Instruction,
                        Operation: "Recording conversation turn",
                        SpeakerName: voice.DisplayName),
                    ct);
                recorded.Add(new ConversationTurnAudio(result.WavData, turn.PauseAfterMs));
            }

            step = "assembling audio";
            await UpdateStateAsync(segment.Id, "Assembling the conversation.", stepTotal, stepTotal, ct);
            var composite = ConversationAssembler.Assemble(recorded);
            var relativePath = Path.Combine("library", ConversationsDirectory, $"{segment.Id}.wav");
            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, composite, ct);
            var duration = WavFile.GetDurationSeconds(composite);

            step = "finalizing";
            await FinalizeAsync(segment.Id, speakers.LeadModeratorId, relativePath, duration, ct);
            logger.LogInformation(
                "Conversation \"{Title}\" produced: {Turns} turn(s), {Duration:F0}s audio",
                segment.Title ?? segment.Topic, turns.Count, duration);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await UpdateStateAsync(
                segment.Id, $"Stopped during shutdown while {step}; will resume.", 0, 0, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.GenerationFailed("conversation");
            await UpdateStateAsync(
                segment.Id, $"Production timed out while {step}; retrying next cycle.", 0, 0, CancellationToken.None);
            logger.LogWarning("Conversation production timed out for {Segment} while {Step}", segment.Id, step);
        }
        catch (Exception ex)
        {
            metrics.GenerationFailed("conversation");
            logger.LogWarning(
                ex, "Conversation production failed for {Segment} while {Step}: {Message}",
                segment.Id, step, ex.GetBaseException().Message);
            await MarkFailedAsync(
                segment.Id, $"Failed while {step}: {ex.GetBaseException().Message}", CancellationToken.None);
        }
    }

    private sealed record ResolvedVoice(
        string DisplayName, string VoiceId, string Engine, string Language, double Rate, string? Instruction);

    private sealed record ResolvedSpeakers(
        IReadOnlyList<ConversationSpeakerBrief> Briefs,
        IReadOnlyDictionary<string, ResolvedVoice> Voices,
        int LeadModeratorId);

    /// <summary>Resolves every participant to a persona brief + a ready designed voice.
    /// Returns null while a member's voice is still being designed (production waits).</summary>
    private async Task<ResolvedSpeakers?> ResolveSpeakersAsync(
        Guid segmentId,
        IReadOnlyList<ConversationParticipant> participants,
        StationSettings settings,
        CancellationToken ct)
    {
        var language = StationLanguages.Normalize(settings.DefaultLanguage);
        var briefs = new List<ConversationSpeakerBrief>();
        var voices = new Dictionary<string, ResolvedVoice>();
        var leadModeratorId = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var participant in participants)
        {
            if (participant.TryGetModeratorId(out var moderatorId))
            {
                var moderator = await db.Moderators.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == moderatorId, ct);
                if (moderator is null)
                {
                    await MarkFailedAsync(segmentId, $"Host \"{participant.DisplayName}\" no longer exists.", ct);
                    return null;
                }

                if (leadModeratorId == 0)
                {
                    leadModeratorId = moderator.Id;
                }

                briefs.Add(new ConversationSpeakerBrief(
                    participant.SpeakerKey,
                    moderator.Name,
                    participant.ConversationRole,
                    $"{moderator.PersonaPrompt} Style: {moderator.Style}."));
                voices[participant.SpeakerKey] = new ResolvedVoice(
                    moderator.Name,
                    moderator.VoiceId,
                    moderator.TtsEngine,
                    language,
                    Math.Clamp(moderator.SpeechRate, 0.7, 1.3),
                    BuildInstruction(moderator.TtsEngine, $"Radio host, {moderator.Style} delivery, in conversation."));
            }
            else if (participant.TryGetArtistMemberId(out var memberId))
            {
                var member = await db.ArtistMembers.AsNoTracking()
                    .Include(m => m.Artist)
                    .FirstOrDefaultAsync(m => m.Id == memberId, ct);
                if (member is null)
                {
                    await MarkFailedAsync(segmentId, $"Guest \"{participant.DisplayName}\" no longer exists.", ct);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(member.VoiceId))
                {
                    memberVoiceQueue.EnqueuePriority(member.Id);
                    await UpdateStateAsync(
                        segmentId, $"Waiting for {member.Name}'s voice to be designed.", 0, 0, ct);
                    return null;
                }

                var background = member.Artist?.DeepBackgroundBiography ?? string.Empty;
                if (background.Length > MaxDeepBackgroundChars)
                {
                    background = background[..MaxDeepBackgroundChars];
                }

                briefs.Add(new ConversationSpeakerBrief(
                    participant.SpeakerKey,
                    member.Name,
                    participant.ConversationRole,
                    $"{member.Role} of {member.Artist?.Name ?? "an artist"}. {member.Biography} {background}".Trim()));
                voices[participant.SpeakerKey] = new ResolvedVoice(
                    member.Name,
                    member.VoiceId!,
                    member.TtsEngine,
                    language,
                    Rate: 1.0,
                    BuildInstruction(member.TtsEngine, "Podcast guest, natural conversational delivery."));
            }
            else
            {
                await MarkFailedAsync(segmentId, $"Unknown speaker key \"{participant.SpeakerKey}\".", ct);
                return null;
            }
        }

        if (leadModeratorId == 0)
        {
            // Announcement.ModeratorId is required — the wrapper needs some host on record.
            leadModeratorId = await db.Moderators.AsNoTracking()
                .Where(m => m.IsActive)
                .OrderBy(m => m.Id)
                .Select(m => m.Id)
                .FirstOrDefaultAsync(ct);
        }

        return new ResolvedSpeakers(briefs, voices, leadModeratorId);
    }

    private async Task<ConversationScript> WriteScriptAsync(
        IServiceProvider services,
        ConversationSegment segment,
        StationSettings settings,
        ResolvedSpeakers speakers,
        CancellationToken ct)
    {
        List<string> recentTitles = [];
        if (segment.PodcastShowId is { } showId)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            recentTitles = await db.ConversationSegments.AsNoTracking()
                .Where(candidate => candidate.PodcastShowId == showId
                    && candidate.Id != segment.Id
                    && candidate.Title != null)
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .Take(RecentEpisodeTitleCount)
                .Select(candidate => candidate.Title!)
                .ToListAsync(ct);
        }

        var writer = services.GetRequiredService<ConversationScriptWriter>();
        return await writer.WriteAsync(
            new ConversationScriptRequest(
                segment.Kind,
                segment.Structure,
                segment.Topic,
                segment.Brief,
                PodcastShowScheduler.NormalizeEpisodeMinutes(segment.TargetDurationMinutes),
                speakers.Briefs,
                Deserialize<ConversationChapter>(segment.ChaptersJson),
                string.IsNullOrWhiteSpace(settings.StationName) ? "WhipRadio" : settings.StationName,
                settings.StationSlogan,
                StationLanguages.Normalize(settings.DefaultLanguage),
                recentTitles),
            ct);
    }

    private async Task FinalizeAsync(
        Guid segmentId, int leadModeratorId, string relativePath, double duration, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var segment = await db.ConversationSegments.FirstAsync(s => s.Id == segmentId, ct);

        var announcement = new Announcement
        {
            Id = Guid.NewGuid(),
            ModeratorId = leadModeratorId,
            Kind = AnnouncementKind.Conversation,
            ScriptText = segment.Transcript ?? string.Empty,
            VoicedText = segment.Transcript ?? string.Empty,
            FilePath = relativePath,
            DurationSeconds = duration,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly,
        };
        db.Announcements.Add(announcement);

        segment.OutputFilePath = relativePath;
        segment.DurationSeconds = duration;
        segment.AnnouncementId = announcement.Id;
        segment.Status = ConversationStatus.Produced;
        segment.ProducedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        segment.ProductionState = null;
        segment.FailureReason = null;
        segment.StepIndex = 0;
        segment.StepTotal = 0;
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishConversationsChangedAsync(ct);
    }

    private async Task UpdateStateAsync(
        Guid segmentId, string state, int stepIndex, int stepTotal, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var segment = await db.ConversationSegments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (segment is null
            || segment.Status is not (ConversationStatus.Planned or ConversationStatus.Scripted))
        {
            return;
        }

        segment.ProductionState = state.Length <= 500 ? state : state[..500];
        segment.StepIndex = stepIndex;
        segment.StepTotal = stepTotal;
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishConversationsChangedAsync(ct);
    }

    private async Task MarkFailedAsync(Guid segmentId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var segment = await db.ConversationSegments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (segment is null)
        {
            return;
        }

        segment.Status = ConversationStatus.Failed;
        segment.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
        segment.ProductionState = null;
        segment.StepIndex = 0;
        segment.StepTotal = 0;
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishConversationsChangedAsync(ct);
    }

    private static string? BuildInstruction(string engine, string instruction)
        => string.Equals(engine, TtsEngines.Qwen, StringComparison.OrdinalIgnoreCase) ? instruction : null;

    private static List<T> Deserialize<T>(string json)
        => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json) ?? [];
}
