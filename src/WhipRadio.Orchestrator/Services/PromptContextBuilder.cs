using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class PromptContextBuilder(
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    TimeProvider timeProvider,
    ICharacterToolCatalog toolCatalog,
    ParticipantMemoryRetriever memoryRetriever,
    ILogger<PromptContextBuilder> logger) : IPromptContextBuilder
{
    public async Task<PromptContext> BuildAsync(PromptContextInput input, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

        ShowContext? show = null;
        try
        {
            show = await schedule.GetCurrentAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve schedule context for prompt context");
        }

        // Chat turns must never inherit the on-air host: a null moderator in Chat
        // scope means the Program Director is speaking, not whoever is on air.
        var moderator = input.Moderator
            ?? (input.Scope == PromptScope.Chat ? null : show?.Moderator);
        // Non-host chat participants (artist members, guests) carry their own
        // persona; the Moderator-only blocks (traits, talk profile, bits, memory
        // layers) stay empty for them.
        var participant = input.Participant;
        var isNonHostParticipant = participant is not null && participant.Moderator is null;
        var format = input.Format ?? show?.Format;
        // The broadcast/written language is the STATION language (from settings). A host's own
        // Language is a per-host attribute (voice accent / occasional native-language guest
        // shows), not what the copy is written in — so it must not drive the script language.
        var language = StationLanguages.Normalize(settings.DefaultLanguage);
        var speechRate = moderator?.SpeechRate ?? 1.0;
        var availableSeconds = input.TargetSeconds ?? show?.RemainingSlotMinutes * 60;
        var wordsPerSecond = PromptWordBudget.WordsPerSecond(language, speechRate);
        var wordBudget = availableSeconds is int seconds
            ? PromptWordBudget.EstimateWordBudget(language, speechRate, seconds)
            : (int?)null;
        var role = ResolveRole(input, moderator);
        var localNow = input.LocalNowOverride ?? timeProvider.GetLocalNow();
        var baselineTraits = moderator is null ? null : MoodEngine.Baseline(moderator);
        var currentTraits = moderator is null ? null : MoodEngine.Current(moderator, localNow);

        ShowWindows? windows = null;
        try
        {
            windows = await schedule.GetShowWindowsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve show windows for prompt context");
        }

        var (currentShowTracks, previousShowTracks) = windows is null
            ? ([], [])
            : await GetShowScopedTracksAsync(db, windows, ct);
        var chatHistory = await GetChatHistoryAsync(db, input.ChatChannelId, settings.ChatHistoryPromptMessages, ct);

        return new PromptContext
        {
            Scope = input.Scope,
            Priority = input.Priority,
            Purpose = ResolvePurpose(input),
            StationName = settings.StationName,
            FrequencyMhz = settings.FrequencyMhz,
            StationSlogan = settings.StationSlogan,
            StationVision = settings.StationVision,
            StationMission = settings.StationMission,
            LocalNow = localNow,
            Language = language,
            FormatName = format?.Name,
            FormatPurpose = FirstNonEmpty(format?.Description, format?.Reason),
            FormatTalkDepth = format?.TalkDepth,
            FormatTalkDensity = format?.TalkDensity ?? format?.Talkativeness,
            RemainingSlotMinutes = show?.RemainingSlotMinutes,
            NextFormatName = show?.NextFormatName,
            HostName = isNonHostParticipant ? participant!.DisplayName : moderator?.Name,
            PersonaSummary = isNonHostParticipant ? participant!.PersonaSummary : moderator?.PersonaPrompt,
            BaselineTraits = baselineTraits,
            CurrentTraits = currentTraits,
            TalkProfile = moderator is null ? null : HostTalkProfile.FromModerator(moderator),
            RelatedTrack = FormatTrack(input.RelatedTrack),
            RelatedTrackFacts = await GetRelatedTrackFactsAsync(db, input.RelatedTrack, ct),
            AlreadySpokenContext = input.AlreadySpokenContext,
            SpeechRate = speechRate,
            WordsPerSecond = wordsPerSecond,
            AvailableSeconds = availableSeconds,
            WordBudget = wordBudget,
            RecentTracks = await GetRecentTracksAsync(db, ct),
            CurrentShowTracks = currentShowTracks,
            PreviousShowTracks = previousShowTracks,
            RecentTalkTopics = await GetRecentTalkTopicsAsync(db, moderator?.Id, ct),
            RecurringBits = await GetRecurringBitsAsync(db, moderator?.Id, ct),
            QueuedListenerMessages = await GetQueuedListenerMessagesAsync(db, ct),
            MemorySlices = await GetCombinedMemorySlicesAsync(db, input, moderator, chatHistory, ct),
            ChatHistory = chatHistory,
            ChatAudience = ResolveChatAudience(input),
            Tools = FilterTools(toolCatalog.GetTools(input.Scope, role), settings),
        };
    }

    /// <summary>Settings-gated tools: the catalog knows scope/role, not station toggles.</summary>
    private static IReadOnlyList<CharacterToolDefinition> FilterTools(
        IReadOnlyList<CharacterToolDefinition> tools, StationSettings settings)
        => settings.PodcastKnowledgeEnabled
            ? tools
            : tools.Where(tool => !string.Equals(tool.Name, "LookupKnowledge", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Factual digest for a real (imported) track, gated by metadata status
    /// (Phase 6a §9): Verified/AutoMatched get the full digest, Matched a
    /// cautious one, everything else nothing. Stored digests stay usable even
    /// while gathering toggles are off — they contain no source prose.
    /// </summary>
    private async Task<string?> GetRelatedTrackFactsAsync(RadioDbContext db, Track? track, CancellationToken ct)
    {
        if (track is null || track.Source == TrackSource.Generated)
        {
            return null;
        }

        var cautious = track.MetadataStatus switch
        {
            MetadataStatus.Verified or MetadataStatus.AutoMatched => false,
            MetadataStatus.Matched => true,
            _ => (bool?)null,
        };
        if (cautious is null)
        {
            return null;
        }

        var qids = await db.ExternalIds.AsNoTracking()
            .Where(e => e.OwnerType == Core.Entities.Metadata.MetadataOwnerType.Track
                && e.OwnerId == track.Id && e.Source == "Wikidata")
            .Select(e => e.Value)
            .ToListAsync(ct);
        if (qids.Count == 0)
        {
            return null;
        }

        var digest = await db.KnowledgeEntries.AsNoTracking()
            .Where(e => qids.Contains(e.SourceEntityId) && e.Digest != "")
            .Select(e => e.Digest)
            .FirstOrDefaultAsync(ct);
        if (digest is null)
        {
            return null;
        }

        return cautious.Value
            ? $"{digest} (Metadata match is unconfirmed — keep factual claims light.)"
            : digest;
    }

    private static CharacterRole ResolveRole(PromptContextInput input, Moderator? moderator)
    {
        if (input.Participant is not null)
        {
            return input.Participant.Role;
        }

        if (input.Scope == PromptScope.ProgramDirector)
        {
            return CharacterRole.ProgramDirector;
        }

        if (input.Scope == PromptScope.Chat && moderator is null)
        {
            return CharacterRole.ProgramDirector;
        }

        if (input.Scope == PromptScope.Chat && moderator is not null)
        {
            if (moderator.IsNewsSpecialist)
            {
                return CharacterRole.NewsSpecialist;
            }

            if (moderator.IsWeatherSpecialist)
            {
                return CharacterRole.WeatherSpecialist;
            }
        }

        if (input.AnnouncementKind == AnnouncementKind.Weather)
        {
            return CharacterRole.WeatherSpecialist;
        }

        if (input.AnnouncementKind == AnnouncementKind.News)
        {
            return CharacterRole.NewsSpecialist;
        }

        return moderator is null ? CharacterRole.System : CharacterRole.Host;
    }

    private static string ResolvePurpose(PromptContextInput input)
        => FirstNonEmpty(input.Purpose, input.AnnouncementKind?.ToString(), input.Scope.ToString())
            ?? input.Scope.ToString();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ResolveChatAudience(PromptContextInput input)
        => FirstNonEmpty(input.ChatCounterpartName, input.ChatAudience?.ToString());

    private static string? FormatTrack(Track? track)
    {
        if (track is null)
        {
            return null;
        }

        // Imported real music has no station Artist entity — its display artist
        // lives on the track itself.
        var artist = track.Artist?.Name ?? track.ImportedArtist;
        return string.IsNullOrWhiteSpace(artist)
            ? $"{track.Title} ({track.Genre})"
            : $"{artist} - {track.Title} ({track.Genre})";
    }

    private static async Task<IReadOnlyList<string>> GetRecentTracksAsync(RadioDbContext db, CancellationToken ct)
    {
        var ids = await db.PlayLog.AsNoTracking()
            .Where(entry => entry.ItemType == PlayoutItemType.Track)
            .OrderByDescending(entry => entry.PlayedAt)
            .Take(5)
            .Select(entry => entry.ItemId)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            return [];
        }

        var tracks = await db.Tracks.AsNoTracking()
            .Include(track => track.Artist)
            .Where(track => ids.Contains(track.Id))
            .ToDictionaryAsync(track => track.Id, ct);

        return ids
            .Select(id => tracks.TryGetValue(id, out var track) ? FormatTrack(track) : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    /// <summary>
    /// Tracks aired in the current and previous show windows, formatted with the
    /// local air time so the host can reference them honestly. These back the
    /// anti-repeat instruction in <see cref="PromptContext.RenderSituation"/>.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Current, IReadOnlyList<string> Previous)> GetShowScopedTracksAsync(
        RadioDbContext db, ShowWindows windows, CancellationToken ct)
    {
        var current = await LoadShowTracksAsync(db, windows.CurrentStartUtc, windows.CurrentEndUtc, 40, ct);
        var previous = windows.PreviousStartUtc is null || windows.PreviousEndUtc is null
            ? []
            : await LoadShowTracksAsync(db, windows.PreviousStartUtc.Value, windows.PreviousEndUtc.Value, 40, ct);
        return (current, previous);
    }

    private static async Task<IReadOnlyList<string>> LoadShowTracksAsync(
        RadioDbContext db, DateTime sinceUtc, DateTime untilUtc, int maxCount, CancellationToken ct)
    {
        var rows = await db.PlayLog.AsNoTracking()
            .Where(entry => entry.ItemType == PlayoutItemType.Track
                && entry.PlayedAt >= sinceUtc
                && entry.PlayedAt < untilUtc)
            .OrderBy(entry => entry.PlayedAt)
            .Take(maxCount)
            .Select(entry => new { entry.ItemId, entry.PlayedAt })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.ItemId).ToList();
        var tracks = await db.Tracks.AsNoTracking()
            .Include(track => track.Artist)
            .Where(track => ids.Contains(track.Id))
            .ToDictionaryAsync(track => track.Id, ct);

        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        return rows
            .Select(row =>
            {
                if (!tracks.TryGetValue(row.ItemId, out var track))
                {
                    return null;
                }

                var artist = track.Artist?.Name;
                var label = string.IsNullOrWhiteSpace(artist)
                    ? $"{track.Title} ({track.Genre})"
                    : $"{artist} - {track.Title} ({track.Genre})";
                var localTime = row.PlayedAt.ToLocalTime();
                return $"{label}, {localTime:HH:mm}";
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> GetRecentTalkTopicsAsync(
        RadioDbContext db,
        int? moderatorId,
        CancellationToken ct)
    {
        var query = db.Announcements.AsNoTracking()
            .OrderByDescending(announcement => announcement.CreatedAt)
            .AsQueryable();

        if (moderatorId is int id)
        {
            query = query.Where(announcement => announcement.ModeratorId == id);
        }

        return await query
            .Take(3)
            .Select(announcement => announcement.ScriptText.Length > 220
                ? announcement.ScriptText.Substring(0, 220)
                : announcement.ScriptText)
            .Where(text => text != "")
            .ToListAsync(ct);
    }

    private DateOnly Today()
        => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    private static async Task<IReadOnlyList<string>> GetRecurringBitsAsync(
        RadioDbContext db,
        int? moderatorId,
        CancellationToken ct)
    {
        if (moderatorId is not int id)
        {
            return [];
        }

        var bits = await db.TalkBits.AsNoTracking()
            .Where(bit => bit.ModeratorId == id && bit.Status == TalkBitStatus.Active)
            .OrderBy(bit => bit.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenBy(bit => bit.PlayCount)
            .Take(5)
            .ToListAsync(ct);

        return bits
            .Select(bit =>
            {
                var lastUsed = bit.LastUsedAtUtc is null
                    ? "never used"
                    : $"last used {bit.LastUsedAtUtc:yyyy-MM-dd}";
                return $"{bit.Premise} (played {bit.PlayCount}, {lastUsed})";
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> GetQueuedListenerMessagesAsync(
        RadioDbContext db,
        CancellationToken ct)
    {
        var messages = await db.ListenerMessages.AsNoTracking()
            .Where(message => message.Status == ListenerMessageStatus.Queued)
            .OrderBy(message => message.SubmittedAt)
            .Take(5)
            .ToListAsync(ct);

        return messages
            .Select(message =>
            {
                var kind = message.Kind == ListenerMessageKind.Request ? "request" : "greeting";
                var request = string.IsNullOrWhiteSpace(message.RequestGenre)
                    ? string.Empty
                    : $", wants {message.RequestGenre}";
                return $"{message.SenderName} ({kind}{request}): {message.MessageText}";
            })
            .ToList();
    }

    /// <summary>
    /// Classic layered host memory plus Phase 5 retrieval: in chat scope the
    /// participant's top-k relevant memories (query = the latest chat message)
    /// are appended — for artist members and guests they are the only memory.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCombinedMemorySlicesAsync(
        RadioDbContext db,
        PromptContextInput input,
        Moderator? moderator,
        IReadOnlyList<string> chatHistory,
        CancellationToken ct)
    {
        var slices = (await GetMemorySlicesAsync(db, moderator?.Id, ct)).ToList();

        if (input.Scope == PromptScope.Chat && chatHistory.Count > 0)
        {
            var participantKey = ResolveParticipantMemoryKey(input, moderator);
            if (participantKey is not null)
            {
                var retrieved = await memoryRetriever.RetrieveAsync(participantKey, chatHistory[^1], k: 3, ct);
                slices.AddRange(retrieved.Select(memory => $"remembered: {memory}"));
            }
        }

        return slices;
    }

    private static string? ResolveParticipantMemoryKey(PromptContextInput input, Moderator? moderator)
    {
        if (input.Participant is { } participant)
        {
            return participant.Kind switch
            {
                ChatParticipantKind.Host when participant.Ref.ModeratorId is int id
                    => ConversationParticipant.HostKey(id),
                ChatParticipantKind.ArtistMember when participant.Ref.EntityId is Guid memberId
                    => ConversationParticipant.MemberKey(memberId),
                ChatParticipantKind.Guest when participant.Ref.EntityId is Guid guestId
                    => ConversationParticipant.GuestKey(guestId),
                _ => null,
            };
        }

        return moderator is null ? null : ConversationParticipant.HostKey(moderator.Id);
    }

    private async Task<IReadOnlyList<string>> GetMemorySlicesAsync(
        RadioDbContext db,
        int? moderatorId,
        CancellationToken ct)
    {
        if (moderatorId is not int id)
        {
            return [];
        }

        var today = Today();
        var dayMemory = await db.ModeratorMemories.AsNoTracking()
            .Where(memory => memory.ModeratorId == id
                && memory.Layer == ModeratorMemoryLayer.DayMemory
                && memory.Date == today)
            .OrderByDescending(memory => memory.CreatedAt)
            .Take(3)
            .Select(memory => $"today: {memory.Content}")
            .ToListAsync(ct);

        var longTermMemory = await db.ModeratorMemories.AsNoTracking()
            .Where(memory => memory.ModeratorId == id
                && memory.Layer == ModeratorMemoryLayer.LongTermMemory)
            .OrderByDescending(memory => memory.CreatedAt)
            .Take(2)
            .Select(memory => $"long-term: {memory.Content}")
            .ToListAsync(ct);

        return dayMemory.Concat(longTermMemory).ToList();
    }

    private static async Task<IReadOnlyList<string>> GetChatHistoryAsync(
        RadioDbContext db,
        Guid? channelId,
        int maxMessages,
        CancellationToken ct)
    {
        if (channelId is null)
        {
            return [];
        }

        int take = Math.Clamp(maxMessages <= 0 ? 20 : maxMessages, 1, 80);
        List<ChatMessage> messages = await db.ChatMessages.AsNoTracking()
            .Include(message => message.SenderModerator)
            .Include(message => message.SenderArtistMember)
            .Include(message => message.SenderGuest)
            .Where(message => message.ChannelId == channelId.Value)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);
        messages.Reverse();

        return messages
            .Select(message =>
            {
                string sender = message.SenderKind switch
                {
                    ChatSenderKind.Admin => "Admin",
                    ChatSenderKind.Host => message.SenderModerator?.Name ?? "Host",
                    ChatSenderKind.ArtistMember => message.SenderArtistMember?.Name ?? "Band member",
                    ChatSenderKind.Guest => message.SenderGuest?.Name ?? "Guest",
                    ChatSenderKind.Director => "Program Director",
                    ChatSenderKind.System => "System",
                    _ => message.SenderKind.ToString(),
                };
                return $"[{message.CreatedAtUtc.ToLocalTime():HH:mm}] {sender}: {message.Text}";
            })
            .ToList();
    }
}
