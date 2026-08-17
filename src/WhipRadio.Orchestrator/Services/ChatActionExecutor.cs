using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed record ChatActionContext(
    ChatChannel Channel,
    ChatMessageDto? AgentMessage,
    ChatParticipant Sender,
    Guid CorrelationId,
    int HopCount)
{
    public ChatSenderKind SenderKind => Sender.SenderKind;

    public CharacterRole SenderRole => Sender.Role;

    /// <summary>Set only when the sender is a host; artists/guests/director have none.</summary>
    public Moderator? SenderModerator => Sender.Moderator;

    /// <summary>
    /// True when this execution replays a Boss-approved <see cref="PendingApproval"/>.
    /// Approval-gated verbs check this to run the real side effect instead of queuing
    /// another approval. Never set from model text — only <c>ApprovalService</c> sets it.
    /// </summary>
    public bool ApprovalGranted { get; init; }
}

public sealed partial class ChatActionExecutor(
    IDbContextFactory<RadioDbContext> dbFactory,
    ICharacterToolCatalog toolCatalog,
    ChatService chat,
    ChatParticipantResolver participants,
    ChatTurnQueue turnQueue,
    TrackQueryService tracks,
    MusicProductionControl musicControl,
    PriorityTalkBreakDispatcher priorityDispatcher,
    ScheduleService schedule,
    DirectorPlanningService director,
    INotificationBus notifications,
    IServiceScopeFactory scopeFactory,
    IPlayoutQueue playoutQueue,
    ModeratorMemoryService moderatorMemory,
    ParticipantMemoryWriter participantMemory,
    IProductionUpdatePublisher productionUpdates,
    ArtistSocialFeedService socialFeed,
    NewsPackageProductionService newsProduction,
    IHubContext<RadioHub> hub,
    TimeProvider timeProvider,
    ILogger<ChatActionExecutor> logger)
{
    public async Task<ChatActionRecord> ExecuteAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        if (toolCatalog.GetTool(call.Name, PromptScope.Chat, context.SenderRole) is null)
        {
            return Failed(call, $"Tool '{call.Name}' is not available to {context.SenderRole}.");
        }

        try
        {
            ChatActionRecord result = call.Name switch
            {
                "Message" => await ExecuteMessageAsync(call, context, ct),
                "Announcement" => await ExecuteAnnouncementAsync(call, context, ct),
                "SearchMusic" => await ExecuteSearchMusicAsync(call, ct),
                "PlanFormat" => await ExecutePlanFormatAsync(call, ct),
                "HireHost" => await ExecuteHireHostAsync(call, ct),
                "AssignHost" => await ExecuteAssignHostAsync(call, ct),
                "StatusReport" => await ExecuteStatusReportAsync(call, ct),
                "Invite" => await ExecuteInviteAsync(call, context, ct),
                "RemoveFromChannel" => await ExecuteRemoveFromChannelAsync(call, context, ct),
                "MakeSong" => await ExecuteMakeSongAsync(call, context, ct),
                "BriefPodcast" => await ExecuteBriefPodcastAsync(call, ct),
                "LookupKnowledge" => await ExecuteLookupKnowledgeAsync(call, ct),
                "SearchArtist" => await ExecuteSearchArtistAsync(call, ct),
                "GetArtistProfile" => await ExecuteGetArtistProfileAsync(call, context, ct),
                "QueueTrack" => await ExecuteQueueTrackAsync(call, context, ct),
                "PlanTalkBreak" => await ExecutePlanTalkBreakAsync(call, context, ct),
                "CreateTalkBit" => await ExecuteCreateTalkBitAsync(call, context, ct),
                "Remember" => await ExecuteRememberAsync(call, context, ct),
                "ProduceNewsPackage" => await ExecuteProduceNewsPackageAsync(call, ct),
                "ProduceWeatherReport" => await ExecuteProduceWeatherReportAsync(call, context, ct),
                "CreateJingle" => await ExecuteCreateJingleAsync(call, ct),
                "SetJingleActive" => await ExecuteSetJingleActiveAsync(call, ct),
                "SetNewsPresenter" => await ExecuteSetPresenterAsync(call, isNews: true, ct),
                "SetWeatherPresenter" => await ExecuteSetPresenterAsync(call, isNews: false, ct),
                "RetireTrack" => await ExecuteRetireTrackAsync(call, ct),
                "PostArtistFeed" => await ExecutePostArtistFeedAsync(call, context, ct),
                "RequestSongFromArtist" => await ExecuteRequestSongFromArtistAsync(call, context, ct),
                "RequestBossApproval" => await ExecuteRequestBossApprovalAsync(call, context, ct),
                "RetireArtist" => await ExecuteRetireArtistAsync(call, ct),
                "DeleteArtist" => await ExecuteDeleteArtistAsync(call, context, ct),
                "DeleteTrack" => await ExecuteDeleteTrackAsync(call, context, ct),
                "DeleteJingle" => await ExecuteDeleteJingleAsync(call, context, ct),
                "RemoveShow" => await ExecuteRemoveShowAsync(call, context, ct),
                "FireHost" => await ExecuteFireHostAsync(call, context, ct),
                "RedefineArtistProfile" => await ExecuteRedefineArtistProfileAsync(call, context, ct),
                "CancelSongProduction" => await ExecuteCancelSongProductionAsync(call, context, ct),
                "EmergencyAnnouncement" => await ExecuteEmergencyAnnouncementAsync(call, context, ct),
                "AnswerListenerMessage" => await ExecuteAnswerListenerMessageAsync(call, context, ct),
                "ManageNewsFeed" => await ExecuteManageNewsFeedAsync(call, context, ct),
                "SetNewsProductionSettings" => await ExecuteSetNewsProductionSettingsAsync(call, context, ct),
                "SetWeatherSettings" => await ExecuteSetWeatherSettingsAsync(call, context, ct),
                "SetStationSettings" => await ExecuteSetStationSettingsAsync(call, context, ct),
                "SetProductionSwitch" => await ExecuteSetProductionSwitchAsync(call, context, ct),
                "SetProviderSettings" => await ExecuteSetProviderSettingsAsync(call, context, ct),
                "StudioStatus" => await ExecuteStudioStatusAsync(call, ct),
                "ServerStatus" => await ExecuteServerStatusAsync(call, ct),
                "PrivacyReport" => await ExecutePrivacyReportAsync(call, ct),
                "MediaCleanupPreview" => await ExecuteMediaCleanupPreviewAsync(call, ct),
                "RunMediaCleanup" => await ExecuteRunMediaCleanupAsync(call, context, ct),
                _ => Failed(call, $"Tool '{call.Name}' has no chat executor."),
            };
            logger.LogInformation(
                "Chat action {Verb} by {Sender} in {Channel}: {Outcome}",
                call.Name,
                context.Sender.DisplayName,
                context.Channel.Name,
                result.ResultSummary);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Log only: the agentic loop feeds the failure back to the agent,
            // which answers the admin itself. Station-channel notifications are
            // reserved for real production failures, not argument mistakes.
            logger.LogWarning(ex, "Chat action {Verb} failed", call.Name);
            return Failed(call, ex.GetBaseException().Message);
        }
    }

    private async Task<ChatActionRecord> ExecuteMessageAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string target = Require(call, "characterId");
        string message = Require(call, "message");
        if (IsAdminTarget(target))
        {
            Guid channelId = context.SenderKind == ChatSenderKind.Director
                ? await chat.GetDirectorChannelIdAsync(ct)
                : context.SenderModerator is { } adminTargetSender
                    ? await chat.GetHostDmChannelIdAsync(adminTargetSender.Id, ct) ?? context.Channel.Id
                    : context.Channel.Id;
            await chat.PostAsync(channelId, context.SenderKind, context.SenderModerator?.Id, message, null, context.CorrelationId, context.HopCount, ct);
            Guid? plannedBreakId = context.Channel.Kind == ChatChannelKind.HostToHost && context.SenderModerator is not null
                ? await CreatePlannedConversationTalkBreakAsync(context, message, ct)
                : null;
            return Succeeded(
                call,
                plannedBreakId is null
                    ? "Message sent to Admin."
                    : $"Message sent to Admin; planned segment {plannedBreakId:N} was created.");
        }

        if (IsDirectorTarget(target))
        {
            if (context.SenderKind == ChatSenderKind.Director)
            {
                return Failed(
                    call,
                    "You are the Program Director yourself - do not forward requests to yourself. "
                    + "Handle the request directly with your own tools.");
            }

            Guid directorChannelId = await chat.GetDirectorChannelIdAsync(ct);
            ChatMessageDto posted = await chat.PostAsync(
                directorChannelId,
                context.SenderKind,
                context.SenderModerator?.Id,
                message,
                null,
                context.CorrelationId,
                context.HopCount + 1,
                ct);
            await TryEnqueueAsync(directorChannelId, null, posted.Id, context, call);
            return Succeeded(call, "Message sent to Program Director.");
        }

        Moderator targetHost = await ResolveHostAsync(target, ct);
        if (context.SenderModerator is { } sender && sender.Id == targetHost.Id)
        {
            return Failed(call, "You cannot send a chat message to yourself.");
        }

        Guid targetChannelId;
        if (context.SenderModerator is { } senderHost)
        {
            targetChannelId = await chat.GetOrCreateHostToHostChannelAsync(senderHost.Id, targetHost.Id, ct);
        }
        else
        {
            targetChannelId = await chat.GetHostDmChannelIdAsync(targetHost.Id, ct)
                ?? throw new InvalidOperationException("Target host DM was not found.");
        }

        ChatMessageDto hostMessage = await chat.PostAsync(
            targetChannelId,
            context.SenderKind,
            context.SenderModerator?.Id,
            message,
            null,
            context.CorrelationId,
            context.HopCount + 1,
            ct);
        await TryEnqueueAsync(targetChannelId, ChatParticipantRef.ForHost(targetHost.Id), hostMessage.Id, context, call);
        return Succeeded(call, $"Message sent to {targetHost.Name}.");
    }

    private async Task<ChatActionRecord> ExecuteAnnouncementAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Moderator moderator = context.SenderModerator
            ?? throw new InvalidOperationException("Announcement requires a host sender.");
        string topic = Require(call, "topic");
        TalkBreakPriority priority = ParsePriority(Optional(call, "priority"));

        ShowContext show = await schedule.GetCurrentAsync(ct);
        if (show.Moderator.Id != moderator.Id)
        {
            return Failed(
                call,
                $"You are currently not in the studio - {show.Moderator.Name} is on air right now. "
                + "You can only make announcements during your own show.");
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

        // Production takes minutes; run it detached so the chat reply is instant.
        // Failures land in the log and the station notification channel, never in
        // the consumer-facing chat.
        ProduceAnnouncementInBackgroundAsync(moderator, topic, priority, settings.StationName).Forget();
        return Succeeded(
            call,
            $"Announcement about '{topic}' is in production ({priority}) and will air in your next talk break.");
    }

    private async Task ProduceAnnouncementInBackgroundAsync(
        Moderator moderator,
        string topic,
        TalkBreakPriority priority,
        string stationName)
    {
        try
        {
            // Fresh scope: the chat turn's scope (and its scoped AnnouncementFactory)
            // is gone long before production finishes.
            using IServiceScope scope = scopeFactory.CreateScope();
            AnnouncementFactory factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            Announcement announcement = await factory.ProduceAsync(
                AnnouncementKind.Banter,
                moderator,
                relatedTrack: null,
                facts: $"The station admin asked for an announcement about: {topic}",
                stationName,
                CancellationToken.None,
                purpose: "chat-requested announcement");

            if (priority is TalkBreakPriority.High or TalkBreakPriority.Emergency)
            {
                await using RadioDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                TalkBreak? talkBreak = await db.TalkBreaks
                    .Include(item => item.Parts)
                    .FirstOrDefaultAsync(item => item.AnnouncementId == announcement.Id);
                if (talkBreak is not null)
                {
                    talkBreak.Priority = priority;
                    talkBreak.ExpiresAtUtc = priority == TalkBreakPriority.Emergency
                        ? timeProvider.GetUtcNow().UtcDateTime.AddHours(1)
                        : timeProvider.GetUtcNow().UtcDateTime.AddHours(24);
                    foreach (TalkPart part in talkBreak.Parts)
                    {
                        part.Priority = priority;
                        part.ExpiresAtUtc = talkBreak.ExpiresAtUtc;
                    }

                    await db.SaveChangesAsync();
                }

                await priorityDispatcher.PushReadyAsync(CancellationToken.None);
            }

            logger.LogInformation(
                "Chat-requested announcement for {Host} produced ({Priority}, {Duration:0}s)",
                moderator.Name,
                priority,
                announcement.DurationSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested announcement for {Host} could not be produced", moderator.Name);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:Announcement",
                $"Announcement for {moderator.Name} could not be produced: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteSearchMusicAsync(CharacterToolCall call, CancellationToken ct)
    {
        string query = Require(call, "query");
        int limit = int.TryParse(Optional(call, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 5;
        IReadOnlyList<TrackSearchResult> results = await tracks.SearchAsync(query, limit, ct);
        if (results.Count == 0)
        {
            return Succeeded(call, "No matching tracks found.");
        }

        string summary = string.Join("; ", results.Select(result =>
            $"{result.ArtistName} - {result.Title} ({result.Genre}, {TimeSpan.FromSeconds(result.DurationSeconds):m\\:ss})"));
        return Succeeded(call, $"{results.Count} track(s): {summary}");
    }

    private async Task<ChatActionRecord> ExecuteLookupKnowledgeAsync(CharacterToolCall call, CancellationToken ct)
    {
        string query = Require(call, "query");
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Defense in depth: PromptContextBuilder already drops the tool when
        // the knowledge setting is off; refuse here too for direct calls.
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        if (!settings.PodcastKnowledgeEnabled)
        {
            return Failed(call, "The knowledge base is switched off in the station settings.");
        }

        var pattern = $"%{query}%";
        var entries = await db.KnowledgeEntries.AsNoTracking()
            .Where(e => EF.Functions.ILike(e.DisplayName, pattern))
            .OrderBy(e => e.DisplayName)
            .Take(3)
            .ToListAsync(ct);
        if (entries.Count == 0)
        {
            // Second try: an imported track title leads to its artist's knowledge.
            var qids = await db.Tracks.AsNoTracking()
                .Where(t => t.Source != TrackSource.Generated && EF.Functions.ILike(t.Title, pattern))
                .Join(
                    db.ExternalIds.AsNoTracking().Where(e =>
                        e.OwnerType == Core.Entities.Metadata.MetadataOwnerType.Track && e.Source == "Wikidata"),
                    track => track.Id,
                    externalId => externalId.OwnerId,
                    (track, externalId) => externalId.Value)
                .Distinct()
                .Take(3)
                .ToListAsync(ct);
            if (qids.Count > 0)
            {
                entries = await db.KnowledgeEntries.AsNoTracking()
                    .Where(e => qids.Contains(e.SourceEntityId))
                    .Take(3)
                    .ToListAsync(ct);
            }
        }

        if (entries.Count == 0)
        {
            return Succeeded(call, $"No gathered knowledge about \"{query}\" yet.");
        }

        var summary = string.Join(" | ", entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Digest))
            .Select(e => $"{e.DisplayName}: {e.Digest}"));
        return Succeeded(
            call,
            string.IsNullOrEmpty(summary)
                ? $"Knowledge entries exist for \"{query}\" but carry no digest yet."
                : $"Background facts (paraphrase in your own words, never quote): {summary}");
    }

    private async Task<ChatActionRecord> ExecutePlanFormatAsync(CharacterToolCall call, CancellationToken ct)
    {
        DayOfWeek day = ParseDay(Require(call, "day"));
        int startMinute = ParseClock(Require(call, "startTime"));
        int duration = int.TryParse(Require(call, "durationMinutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException("durationMinutes must be a number.");
        int? hostId = null;
        string? hostArg = Optional(call, "host");
        if (!string.IsNullOrWhiteSpace(hostArg))
        {
            hostId = (await director.ResolveHostAsync(hostArg, ct)).Id;
        }

        SlotPlanResult result = await director.PlanSlotAsync(
            day,
            startMinute,
            duration,
            Require(call, "genre"),
            Optional(call, "name"),
            Optional(call, "description"),
            hostId,
            "planned by director chat",
            ct);
        return Succeeded(call, result.Summary);
    }

    private async Task<ChatActionRecord> ExecuteHireHostAsync(CharacterToolCall call, CancellationToken ct)
    {
        SpecialistHostRole role = ParseHostRole(Optional(call, "role"));
        Moderator moderator = await director.HireHostAsync(Require(call, "brief"), role, ct);
        string roleLabel = role switch
        {
            SpecialistHostRole.News => "news specialist",
            SpecialistHostRole.Weather => "weather specialist",
            _ => "host",
        };
        return Succeeded(call, $"Hired {moderator.Name} as {roleLabel}; voice is ready.");
    }

    private async Task<ChatActionRecord> ExecuteAssignHostAsync(CharacterToolCall call, CancellationToken ct)
    {
        Format format = await director.ResolveFormatAsync(Require(call, "format"), ct);
        Moderator moderator = await director.ResolveHostAsync(Require(call, "host"), ct);
        await director.AssignHostAsync(format.Id, moderator.Id, ct);
        return Succeeded(call, $"Assigned {moderator.Name} to {format.Name}.");
    }

    private async Task<ChatActionRecord> ExecuteStatusReportAsync(CharacterToolCall call, CancellationToken ct)
        => Succeeded(call, await director.BuildStatusReportAsync(ct));

    private async Task<ChatActionRecord> ExecuteInviteAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        ChatParticipant invitee = await ResolveParticipantByNameAsync(Require(call, "participant"), ct);
        if (invitee.Kind == ChatParticipantKind.Director)
        {
            return Failed(call, "The Program Director is reachable in every channel and cannot be invited.");
        }

        Guid channelId = await ResolveGroupChannelAsync(context, Optional(call, "channel"), ct);
        bool added = await chat.AddMemberAsync(channelId, invitee.Ref, invitee.DisplayName, ct);
        return Succeeded(
            call,
            added
                ? $"{invitee.DisplayName} joined the group."
                : $"{invitee.DisplayName} is already in the group.");
    }

    private async Task<ChatActionRecord> ExecuteRemoveFromChannelAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        ChatParticipant target = await ResolveParticipantByNameAsync(Require(call, "participant"), ct);
        Guid channelId = await ResolveGroupChannelAsync(context, Optional(call, "channel"), ct);
        bool removed = await chat.RemoveMemberAsync(channelId, target.Ref, ct);
        return removed
            ? Succeeded(call, $"{target.DisplayName} left the group.")
            : Failed(call, $"{target.DisplayName} is not a member of that group.");
    }

    private async Task<ChatActionRecord> ExecuteMakeSongAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string? hint = Optional(call, "hint");
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Artist artist;
        if (context.SenderRole == CharacterRole.Artist)
        {
            Guid memberId = context.Sender.Ref.EntityId
                ?? throw new InvalidOperationException("The artist sender has no member identity.");
            artist = await db.ArtistMembers.AsNoTracking()
                .Where(member => member.Id == memberId)
                .Select(member => member.Artist!)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Your band no longer exists in the library.");
        }
        else
        {
            string artistName = Require(call, "artist");
            string lowered = artistName.Trim().ToLowerInvariant();
            artist = await db.Artists.AsNoTracking()
                .Where(candidate => !candidate.IsRetired && candidate.Name.ToLower() == lowered)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Artist '{artistName}' was not found.");
        }

        if (artist.IsRetired)
        {
            return Failed(call, $"{artist.Name} is retired and no longer records.");
        }

        musicControl.RequestTrackFor(new ManualSongRequest(artist.Id, hint));
        return Succeeded(
            call,
            hint is null
                ? $"Song for {artist.Name} queued in the studio."
                : $"Song for {artist.Name} queued in the studio (direction: {hint}).");
    }

    private async Task<ChatActionRecord> ExecuteBriefPodcastAsync(CharacterToolCall call, CancellationToken ct)
    {
        string topic = Require(call, "topic");
        string brief = Optional(call, "brief") ?? topic;
        int durationMinutes = int.TryParse(
            Optional(call, "durationMinutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMinutes)
            ? Math.Clamp(parsedMinutes, 10, 30)
            : 15;

        List<ConversationParticipant> speakers = await ResolvePodcastParticipantsAsync(
            Require(call, "participants"), ct);

        (List<Guid> trackIds, List<string> trackNotes) = await ResolveReferencedTracksAsync(Optional(call, "tracks"), ct);
        string fullBrief = trackNotes.Count == 0
            ? brief
            : $"{brief}\n\nTalk about these songs from the station library (they will play around the episode):\n- "
                + string.Join("\n- ", trackNotes);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ConversationSegment segment = new()
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Podcast,
            Structure = ConversationStructure.Freeform,
            Topic = topic,
            Brief = fullBrief,
            TargetDurationMinutes = durationMinutes,
            ParticipantsJson = System.Text.Json.JsonSerializer.Serialize(speakers),
            ReferencedTrackIdsJson = System.Text.Json.JsonSerializer.Serialize(trackIds),
            Status = ConversationStatus.Planned,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
        db.ConversationSegments.Add(segment);
        await db.SaveChangesAsync(ct);

        string speakerNames = string.Join(", ", speakers.Select(speaker => speaker.DisplayName));
        string trackSummary = trackIds.Count == 0 ? "no referenced tracks" : $"{trackIds.Count} referenced track(s)";
        return Succeeded(
            call,
            $"Podcast \"{topic}\" briefed with {speakers.Count} speakers ({speakerNames}), {durationMinutes} min, "
            + $"{trackSummary}. It goes into production now; air it from the Podcasts page when ready.");
    }

    /// <summary>
    /// Resolves comma-separated names to speakers. A band name expands to its
    /// voiced members (firm rule: a band in a talk needs enough voiced members).
    /// </summary>
    private async Task<List<ConversationParticipant>> ResolvePodcastParticipantsAsync(
        string participantsArgument,
        CancellationToken ct)
    {
        const int maxSpeakers = 5;
        string[] names = participantsArgument
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ConversationParticipant> speakers = [];

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        foreach (string name in names)
        {
            ChatParticipant? person = await participants.ResolveByNameAsync(name, ct);
            if (person is not null && person.Kind != ChatParticipantKind.Director)
            {
                AddSpeaker(speakers, ToConversationParticipant(person));
                continue;
            }

            // Not a person — maybe a band name that expands to its voiced members.
            string lowered = name.ToLowerInvariant();
            Artist? band = await db.Artists.AsNoTracking()
                .Include(artist => artist.Members)
                .FirstOrDefaultAsync(artist => !artist.IsRetired && artist.Name.ToLower() == lowered, ct);
            if (band is null)
            {
                throw new InvalidOperationException(
                    $"No host, band member, guest, or band named '{name}' was found.");
            }

            List<ArtistMember> voiced = band.Members
                .Where(member => !string.IsNullOrWhiteSpace(member.VoiceId))
                .OrderBy(member => member.SortOrder)
                .ToList();
            if (voiced.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{band.Name} has no members with a designed voice yet - their voices are still in the booth. "
                    + "Name individual members instead, or try again later.");
            }

            foreach (ArtistMember member in voiced)
            {
                if (speakers.Count >= maxSpeakers)
                {
                    break;
                }

                AddSpeaker(speakers, new ConversationParticipant
                {
                    SpeakerKey = ConversationParticipant.MemberKey(member.Id),
                    DisplayName = member.Name,
                    ConversationRole = "Guest",
                });
            }
        }

        if (speakers.Count is < 2 or > maxSpeakers)
        {
            throw new InvalidOperationException(
                $"A podcast needs 2-{maxSpeakers} speakers; '{participantsArgument}' resolved to {speakers.Count}.");
        }

        return speakers;
    }

    private static void AddSpeaker(List<ConversationParticipant> speakers, ConversationParticipant candidate)
    {
        if (speakers.Any(existing => existing.SpeakerKey == candidate.SpeakerKey))
        {
            return;
        }

        if (speakers.Count >= 5)
        {
            throw new InvalidOperationException("A podcast supports at most 5 speakers.");
        }

        speakers.Add(candidate);
    }

    private static ConversationParticipant ToConversationParticipant(ChatParticipant person)
        => new()
        {
            SpeakerKey = person.Kind switch
            {
                ChatParticipantKind.Host => ConversationParticipant.HostKey(person.Ref.ModeratorId!.Value),
                ChatParticipantKind.ArtistMember => ConversationParticipant.MemberKey(person.Ref.EntityId!.Value),
                _ => ConversationParticipant.GuestKey(person.Ref.EntityId!.Value),
            },
            DisplayName = person.DisplayName,
            ConversationRole = person.Kind == ChatParticipantKind.Host ? "Host" : "Guest",
        };

    private async Task<(List<Guid> Ids, List<string> Notes)> ResolveReferencedTracksAsync(
        string? tracksArgument,
        CancellationToken ct)
    {
        List<Guid> ids = [];
        List<string> notes = [];
        if (string.IsNullOrWhiteSpace(tracksArgument))
        {
            return (ids, notes);
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        foreach (string title in tracksArgument.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string lowered = title.ToLowerInvariant();
            Track? track = await db.Tracks.AsNoTracking()
                .Include(candidate => candidate.Artist)
                .Where(candidate => !candidate.IsRetired && candidate.Title.ToLower().Contains(lowered))
                .OrderBy(candidate => candidate.Title.Length)
                .FirstOrDefaultAsync(ct);
            if (track is null)
            {
                throw new InvalidOperationException($"Track '{title}' was not found in the library.");
            }

            if (ids.Contains(track.Id))
            {
                continue;
            }

            ids.Add(track.Id);
            string story = string.IsNullOrWhiteSpace(track.SongStory) ? "" : $" Story: {track.SongStory}";
            notes.Add($"\"{track.Title}\" by {track.Artist?.Name ?? "unknown"} ({track.Genre}).{story}");
        }

        return (ids, notes);
    }

    private async Task<ChatParticipant> ResolveParticipantByNameAsync(string name, CancellationToken ct)
        => await participants.ResolveByNameAsync(name, ct)
            ?? throw new InvalidOperationException(
                $"No host, band member, or guest named '{name}' was found.");

    /// <summary>The current channel when it is a group; otherwise a group resolved by name.</summary>
    private async Task<Guid> ResolveGroupChannelAsync(
        ChatActionContext context,
        string? channelName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return context.Channel.Kind == ChatChannelKind.Group
                ? context.Channel.Id
                : throw new InvalidOperationException(
                    "This is not a group channel. Name the target group with the 'channel' argument.");
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        string lowered = channelName.Trim().ToLowerInvariant();
        Guid channelId = await db.ChatChannels.AsNoTracking()
            .Where(channel => channel.Kind == ChatChannelKind.Group
                && !channel.IsArchived
                && channel.Name.ToLower() == lowered)
            .Select(channel => channel.Id)
            .FirstOrDefaultAsync(ct);
        return channelId != Guid.Empty
            ? channelId
            : throw new InvalidOperationException($"Group channel '{channelName}' was not found.");
    }

    private async Task TryEnqueueAsync(
        Guid channelId,
        ChatParticipantRef? responder,
        Guid triggerMessageId,
        ChatActionContext context,
        CharacterToolCall call)
    {
        try
        {
            await using RadioDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(CancellationToken.None);
            if (context.HopCount + 1 > settings.ChatMaxAgentHops)
            {
                await chat.PostAsync(
                    channelId,
                    ChatSenderKind.System,
                    null,
                    $"Agent exchange stopped at hop cap ({settings.ChatMaxAgentHops}).",
                    null,
                    context.CorrelationId,
                    context.HopCount + 1,
                    CancellationToken.None);
                return;
            }

            turnQueue.TryEnqueue(new ChatTurnRequest(
                channelId,
                responder,
                triggerMessageId,
                context.CorrelationId,
                context.HopCount + 1));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue chat response for tool {Tool}", call.Name);
        }
    }

    private async Task<Moderator> ResolveHostAsync(string value, CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            Moderator? byId = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(host => host.Id == id && host.IsActive, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await db.Moderators.AsNoTracking()
            .Where(host => host.IsActive)
            .OrderBy(host => host.Name)
            .FirstOrDefaultAsync(host => host.Name.ToLower() == value.Trim().ToLower(), ct)
            ?? throw new InvalidOperationException($"Active host '{value}' was not found.");
    }

    private async Task<Guid> CreatePlannedConversationTalkBreakAsync(
        ChatActionContext context,
        string report,
        CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ChatChannel channel = await db.ChatChannels.AsNoTracking()
            .Include(item => item.Moderator)
            .Include(item => item.CounterpartModerator)
            .FirstAsync(item => item.Id == context.Channel.Id, ct);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime expiresAt = now.AddDays(7);
        string left = channel.Moderator?.Name ?? "Host";
        string right = channel.CounterpartModerator?.Name ?? "Host";
        string topic = NormalizeTopic(report);
        string purpose = $"planned two-host segment: {topic}";

        TalkBreak talkBreak = new()
        {
            Id = Guid.NewGuid(),
            ModeratorId = context.SenderModerator!.Id,
            Priority = TalkBreakPriority.Scheduled,
            Status = TalkBreakStatus.Pending,
            Purpose = purpose,
            Title = $"Planned two-host segment: {left} + {right}",
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = TalkPartKind.Banter,
                    Status = TalkPartStatus.Pending,
                    Priority = TalkBreakPriority.Scheduled,
                    Purpose = purpose,
                    DesiredDurationSeconds = 180,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = expiresAt,
                },
            ],
        };
        db.TalkBreaks.Add(talkBreak);
        await db.SaveChangesAsync(ct);
        return talkBreak.Id;
    }

    private static ChatActionRecord Succeeded(CharacterToolCall call, string summary)
        => new(call.Name, call.Arguments, ChatActionState.Succeeded, summary, DateTime.UtcNow);

    private static ChatActionRecord Failed(CharacterToolCall call, string summary)
        => new(call.Name, call.Arguments, ChatActionState.Failed, summary, DateTime.UtcNow);

    private static string Require(CharacterToolCall call, string name)
        => call.Arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Tool '{call.Name}' is missing required argument '{name}'.");

    private static string? Optional(CharacterToolCall call, string name)
        => call.Arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string NormalizeTopic(string value)
    {
        string oneLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(oneLine))
        {
            return "topic agreed in host chat";
        }

        return oneLine.Length <= 180 ? oneLine : $"{oneLine[..177]}...";
    }

    private static bool IsAdminTarget(string value)
        => value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
            || value.Equals("User", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectorTarget(string value)
        => value.Equals("Director", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Program Director", StringComparison.OrdinalIgnoreCase);

    private static TalkBreakPriority ParsePriority(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "high" => TalkBreakPriority.High,
            "emergency" => TalkBreakPriority.Emergency,
            _ => TalkBreakPriority.Normal,
        };

    // The agents mirror the admin's language (per D4), so day arguments arrive
    // in German just as often as English.
    private DayOfWeek ParseDay(string value)
    {
        if (Enum.TryParse(value, ignoreCase: true, out DayOfWeek day))
        {
            return day;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "mon" or "montag" or "mo" => DayOfWeek.Monday,
            "tue" or "tues" or "dienstag" or "di" => DayOfWeek.Tuesday,
            "wed" or "mittwoch" or "mi" => DayOfWeek.Wednesday,
            "thu" or "thur" or "thurs" or "donnerstag" or "do" => DayOfWeek.Thursday,
            "fri" or "freitag" or "fr" => DayOfWeek.Friday,
            "sat" or "samstag" or "sonnabend" or "sa" => DayOfWeek.Saturday,
            "sun" or "sonntag" or "so" => DayOfWeek.Sunday,
            "today" or "heute" => timeProvider.GetLocalNow().DayOfWeek,
            "tomorrow" or "morgen" => timeProvider.GetLocalNow().AddDays(1).DayOfWeek,
            _ => throw new InvalidOperationException(
                $"Day '{value}' is not valid. Use an English or German day name, 'today', or 'tomorrow'."),
        };
    }

    private static int ParseClock(string value)
    {
        string[] parts = value.Trim().Split(':', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute))
        {
            throw new InvalidOperationException("startTime must be HH:mm.");
        }

        return Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
    }
}
