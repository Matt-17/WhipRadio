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

        var moderator = input.Moderator ?? show?.Moderator;
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
            HostName = moderator?.Name,
            PersonaSummary = moderator?.PersonaPrompt,
            BaselineTraits = baselineTraits,
            CurrentTraits = currentTraits,
            TalkProfile = moderator is null ? null : HostTalkProfile.FromModerator(moderator),
            RelatedTrack = FormatTrack(input.RelatedTrack),
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
            MemorySlices = await GetMemorySlicesAsync(db, moderator?.Id, ct),
            Tools = toolCatalog.GetTools(input.Scope, role),
        };
    }

    private static CharacterRole ResolveRole(PromptContextInput input, Moderator? moderator)
    {
        if (input.Scope == PromptScope.ProgramDirector)
        {
            return CharacterRole.ProgramDirector;
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

    private static string? FormatTrack(Track? track)
    {
        if (track is null)
        {
            return null;
        }

        var artist = track.Artist?.Name;
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
}
