using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The conductor: resolves the current show context (format → host → genre),
/// picks the next track and a fitting talk segment, and feeds the playout queue
/// strictly sequentially. Talks vary: intros, back-announces, weather,
/// personal notes, and a proper handover when the host changes.
/// </summary>
public class ShowRunnerService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    IPlayoutQueue playoutQueue,
    PriorityTalkBreakDispatcher priorityTalkBreakDispatcher,
    TimeProvider timeProvider,
    ILogger<ShowRunnerService> logger) : BackgroundService
{
    /// <summary>If the queue already holds ≥2 items, wait.</summary>
    public const int MaxQueueDepth = 2;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdStartDelay = TimeSpan.FromSeconds(20);
    // Generous enough for Qwen TTS at ~1.7x realtime on a busy GPU: a 60 s
    // story talk renders in ~100 s — the budget must not cancel it.
    private static readonly TimeSpan SyncProductionBudget = TimeSpan.FromSeconds(150);

    private readonly Queue<Guid> _recentlyEnqueued = new();
    private int _recentlyEnqueuedCap = 3;
    private int _tracksSinceAnnouncement;
    private int _previousModeratorId = -1;
    private Track? _lastEnqueuedTrack;

    private sealed record ReadyDedication(ListenerMessage Message, Track Track);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ShowRunner cycle failed ({Reason}); retrying", ex.GetBaseException().Message);
                delay = IdleDelay;
            }

            await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.PlayoutEnabled)
        {
            return IdleDelay; // taken off air from the admin page
        }

        _recentlyEnqueuedCap = settings.RecentExclusionCount;

        using var scope = scopeFactory.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<ITrackSelector>();
        var context = await schedule.GetCurrentAsync(ct);

        // Enrich the show context with diversity inputs: the current/previous show
        // time windows (for hard no-repeat) and the station selection settings.
        ShowWindows? windows = null;
        try
        {
            windows = await schedule.GetShowWindowsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve show windows for track selection");
        }

        context = context with
        {
            ShowWindows = windows,
            Selection = MapSelectionSettings(settings),
        };

        // Host changed since the last cycle → proper on-air handover first.
        if (_previousModeratorId >= 0 && _previousModeratorId != context.Moderator.Id && playoutQueue.Count < MaxQueueDepth)
        {
            await EnqueueHostChangeAsync(scope, context.Moderator, _previousModeratorId, settings.StationName, ct);
        }

        _previousModeratorId = context.Moderator.Id;

        // High and emergency TalkBreaks jump the queue: right after the current item.
        await priorityTalkBreakDispatcher.PushReadyAsync(ct);

        // A fulfilled music request takes over the next slot: dedication talk + THE track.
        var dedication = await FindReadyDedicationAsync(ct);
        var track = dedication?.Track ?? await PickTrackAsync(selector, context, ct);

        var action = ShowPlanner.Decide(new ShowPlannerInput(
            playoutQueue.Count,
            MaxQueueDepth,
            TrackAvailable: track is not null,
            _tracksSinceAnnouncement,
            settings.AnnouncementEveryNTracks,
            PriorityTalkPending: dedication is not null));

        switch (action)
        {
            case ShowAction.Wait:
                return IdleDelay;

            case ShowAction.EnqueueFillerTalk:
                await EnqueueFillerTalkAsync(scope, context.Moderator, settings.StationName, ct);
                return ColdStartDelay;

            case ShowAction.EnqueueTrackWithIntro:
                if (dedication is not null)
                {
                    await EnqueueDedicationAsync(scope, dedication, context.Moderator, settings.StationName, ct);
                    _tracksSinceAnnouncement = 0;
                    return TimeSpan.Zero;
                }

                var talks = await PlanGapTalksAsync(scope, track!, context, settings, ct);
                var playoutTalks = await RenderGapTalksAsync(scope, talks, context.Moderator, ct);
                foreach (var talk in playoutTalks)
                {
                    var talkModerator = await ResolveModeratorForAnnouncementAsync(talk, context.Moderator, ct);
                    playoutQueue.Enqueue(ToPlayoutItem(talk, talkModerator));
                }

                _tracksSinceAnnouncement = talks.Count > 0 ? 0 : _tracksSinceAnnouncement + 1;
                EnqueueTrack(track!, context.Moderator);
                return TimeSpan.Zero;

            case ShowAction.EnqueueTrackOnly:
                EnqueueTrack(track!, context.Moderator);
                _tracksSinceAnnouncement++;
                return TimeSpan.Zero;

            default:
                return IdleDelay;
        }
    }

    private async Task<ReadyDedication?> FindReadyDedicationAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var message = await db.ListenerMessages.AsNoTracking()
            .Where(m => m.Status == ListenerMessageStatus.Queued && m.FulfilledByTrackId != null)
            .OrderBy(m => m.SubmittedAt)
            .FirstOrDefaultAsync(ct);
        if (message is null)
        {
            return null;
        }

        var track = await db.Tracks.AsNoTracking()
            .Include(t => t.Artist)
            .FirstOrDefaultAsync(t => t.Id == message.FulfilledByTrackId && !t.IsRetired, ct);
        if (track is null)
        {
            // Track vanished/retired — release the request back to the mailbag path.
            await db.ListenerMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.FulfilledByTrackId, (Guid?)null), ct);
            return null;
        }

        return new ReadyDedication(message, track);
    }

    /// <summary>
    /// Airs a fulfilled request: dedication talk immediately followed by the
    /// requested track. If the talk fails to produce, the track still plays.
    /// </summary>
    private async Task EnqueueDedicationAsync(
        IServiceScope scope, ReadyDedication dedication, Moderator moderator, string stationName, CancellationToken ct)
    {
        var (message, track) = dedication;
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SyncProductionBudget);

            var talk = await factory.ProduceAsync(
                AnnouncementKind.RequestDedication, moderator, track,
                $"{message.SenderName}|{message.MessageText}|{message.RequestGenre}",
                stationName, budget.Token);

            playoutQueue.Enqueue(ToPlayoutItem(talk, moderator));
            await MarkRequestOnAirAsync(message.Id, moderator.Id, talk.Id, ct);
            logger.LogInformation(
                "Dedication: \"{Title}\" airs for {Sender} right after the announcement", track.Title, message.SenderName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Dedication talk failed; playing the requested track without it");
            await MarkRequestOnAirAsync(message.Id, moderator.Id, announcementId: null, ct);
        }

        EnqueueTrack(track, moderator);
    }

    private async Task MarkRequestOnAirAsync(Guid messageId, int moderatorId, Guid? announcementId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ListenerMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, ListenerMessageStatus.OnAir)
                .SetProperty(m => m.ModeratorId, moderatorId)
                .SetProperty(m => m.AnnouncementId, announcementId), ct);
    }

    private async Task<StationSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
    }

    private async Task<Track?> PickTrackAsync(ITrackSelector selector, ShowContext context, CancellationToken ct)
    {
        Track? track = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            track = await selector.PickNextAsync(context, ct);
            if (track is null || !_recentlyEnqueued.Contains(track.Id))
            {
                break; // small library may force repeats — accepted after retries
            }
        }

        return track;
    }

    private void EnqueueTrack(Track track, Moderator moderator)
    {
        playoutQueue.Enqueue(new PlayoutItem(
            PlayoutItemType.Track, track.Id, track.FilePath, track.Title, track.DurationSeconds, moderator.Id));

        var cap = Math.Max(3, _recentlyEnqueuedCap);
        _recentlyEnqueued.Enqueue(track.Id);
        while (_recentlyEnqueued.Count > cap)
        {
            _recentlyEnqueued.Dequeue();
        }

        _lastEnqueuedTrack = track;
        logger.LogInformation("Enqueued track \"{Title}\"", track.Title);
    }

    private static SelectionSettings MapSelectionSettings(StationSettings settings) => new()
    {
        FatigueFactor = settings.FatigueFactor,
        MaxArtistPlaysPerHour = settings.DefaultMaxArtistPlaysPerHour,
        ArtistLookbackTracks = settings.DefaultArtistLookbackTracks,
        SubgenreRotation = settings.SelectionDiversityEnabled,
        PreferHostGenres = settings.SelectionDiversityEnabled,
        DiversityEnabled = settings.SelectionDiversityEnabled,
        RecentExclusionCount = settings.RecentExclusionCount,
    };

    /// <summary>
    /// Plans the talk chain for the gap before the next track. Talks are produced
    /// FRESH for this gap — nothing stale from a pool. The hourly weather report
    /// comes first when due (listener greetings don't pass through here anymore;
    /// they jump straight to the queue front), then the host's mood decides 0–3
    /// free talks ("that was …", a coffee story, "up next …") with varying
    /// lengths, scaled by the host's and format's talkativeness.
    /// </summary>
    private async Task<List<Announcement>> PlanGapTalksAsync(
        IServiceScope scope, Track nextTrack, ShowContext context, StationSettings settings, CancellationToken ct)
    {
        var moderator = context.Moderator;
        var talks = new List<Announcement>();
        var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Weather: once per hour, first talk slot after the full hour — and only
            // a freshly prepared report (≤ 30 min old), never a stale one.
            // Skip gap-talk weather when a scheduled top-of-hour package (which may
            // be weather-only at :30) already covers this window.
            var localNow = timeProvider.GetLocalNow();
            var windowStartUtc = WeatherScheduler.CurrentWindowStart(localNow, settings.WeatherCadenceMinutes).UtcDateTime;
            var hasScheduledPackage = await db.NewsPackages.AsNoTracking()
                .AnyAsync(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc == windowStartUtc
                    && (package.Status == NewsPackageStatus.Ready
                        || package.Status == NewsPackageStatus.Queued
                        || package.Status == NewsPackageStatus.Played), ct);
            if (settings.WeatherEnabled
                && !hasScheduledPackage
                && WeatherScheduler.IsAirWindow(localNow, settings.WeatherCadenceMinutes)
                && !await WeatherAiredThisWindowAsync(db, localNow, settings.WeatherCadenceMinutes, ct))
            {
                var weather = await FindFreshWeatherReportAsync(db, settings.WeatherCadenceMinutes, ct);
                if (weather is not null)
                {
                    if (weather.ModeratorId != moderator.Id && !settings.WeatherFullHandoverEnabled)
                    {
                        var weatherHost = await db.Moderators.AsNoTracking()
                            .FirstOrDefaultAsync(host => host.Id == weather.ModeratorId, ct);
                        if (weatherHost is not null)
                        {
                            talks.Add(await ProduceWeatherHandoffAsync(
                                factory,
                                moderator,
                                weatherHost,
                                settings.StationName,
                                ct));
                        }
                    }

                    talks.Add(weather);
                }
            }
        }

        // The host's mood: 0–3 fresh talks, scaled by host + format talkativeness.
        var talkativeness = TalkPlanner.EffectiveTalkativeness(moderator, context.Format);
        var talkProfile = HostTalkProfile.FromModerator(moderator);
        var talkDepth = context.Format?.TalkDepth ?? TalkDepth.Light;
        var freeTalkCount = TalkPlanner.PickGapTalkCount(Random.Shared, talks.Count > 0, moderator, context.Format);

        var talkBitRuntime = scope.ServiceProvider.GetRequiredService<TalkBitRuntimeService>();
        var preferTalkBit = freeTalkCount > 0
            && talkProfile.Allows(AnnouncementKind.TalkBit)
            && Random.Shared.NextDouble() < talkProfile.EvergreenBitTolerance;
        var introDone = false;

        for (var i = 0; i < freeTalkCount; i++)
        {
            var kind = preferTalkBit && i == 0
                ? AnnouncementKind.TalkBit
                : TalkPlanner.PickFreeTalkKind(
                    Random.Shared,
                    hasNextTrack: !introDone,
                    hasPreviousTrack: _lastEnqueuedTrack is not null && i == 0,
                    talkProfile); // "that was ..." only opens a gap

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(SyncProductionBudget);

                if (kind == AnnouncementKind.TalkBit)
                {
                    var bitTalk = await talkBitRuntime.TryProduceAsync(
                        moderator,
                        settings.StationName,
                        budget.Token,
                        TalkPlanner.PickLengthHint(Random.Shared, talkDepth, talkativeness));
                    if (bitTalk is not null)
                    {
                        talks.Add(bitTalk);
                        continue;
                    }

                    kind = TalkPlanner.PickFreeTalkKind(
                        Random.Shared,
                        hasNextTrack: !introDone,
                        hasPreviousTrack: _lastEnqueuedTrack is not null && i == 0,
                        talkProfile);
                    if (kind == AnnouncementKind.TalkBit)
                    {
                        continue;
                    }
                }

                var relatedTrack = kind switch
                {
                    AnnouncementKind.SongIntro => nextTrack,
                    AnnouncementKind.SongOutro => _lastEnqueuedTrack,
                    _ => null,
                };

                var talk = await factory.ProduceAsync(
                    kind, moderator, relatedTrack, null, settings.StationName, budget.Token,
                    lengthHint: TalkPlanner.PickLengthHint(Random.Shared, talkDepth, talkativeness));
                talks.Add(talk);
                introDone |= kind == AnnouncementKind.SongIntro;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Fresh {Kind} talk failed; continuing the gap without it", kind);
            }
        }

        return talks;
    }

    private async Task<IReadOnlyList<Announcement>> RenderGapTalksAsync(
        IServiceScope scope,
        List<Announcement> talks,
        Moderator fallbackModerator,
        CancellationToken ct)
    {
        if (talks.Count <= 1)
        {
            return talks;
        }

        try
        {
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();
            return [await renderer.RenderAsync(talks, fallbackModerator, ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Talk segment rendering failed for {Count} part(s); queueing individual announcements",
                talks.Count);
            return talks;
        }
    }

    private async Task<Announcement?> FindFreshWeatherReportAsync(
        RadioDbContext db,
        int cadenceMinutes,
        CancellationToken ct)
    {
        var freshCutoff = timeProvider.GetUtcNow().UtcDateTime
            .AddMinutes(-WeatherScheduler.NormalizeCadence(cadenceMinutes));
        var weatherIds = await db.TalkParts.AsNoTracking()
            .Where(part => part.Kind == TalkPartKind.Weather
                && part.Purpose == "WeatherReport"
                && part.Status == TalkPartStatus.Rendered
                && part.AnnouncementId != null)
            .Select(part => part.AnnouncementId!.Value)
            .ToListAsync(ct);

        return weatherIds.Count == 0
            ? null
            : await ImmediatePlayableAnnouncements(db.Announcements.AsNoTracking(), weatherIds, freshCutoff)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
    }

    internal static IQueryable<Announcement> ImmediatePlayableAnnouncements(
        IQueryable<Announcement> announcements,
        IReadOnlyList<Guid> candidateIds,
        DateTime freshCutoff)
        => announcements.Where(announcement => candidateIds.Contains(announcement.Id)
            && !announcement.WasPlayed
            && announcement.CreatedAt >= freshCutoff
            && announcement.PlayoutIntent == AnnouncementPlayoutIntent.Immediate);

    private static async Task<bool> WeatherAiredThisWindowAsync(
        RadioDbContext db, DateTimeOffset localNow, int cadenceMinutes, CancellationToken ct)
    {
        var windowStartUtc = WeatherScheduler.CurrentWindowStart(localNow, cadenceMinutes).UtcDateTime;
        var airedIds = await db.PlayLog.AsNoTracking()
            .Where(e => e.PlayedAt >= windowStartUtc && e.ItemType == PlayoutItemType.Announcement)
            .Select(e => e.ItemId)
            .ToListAsync(ct);
        if (airedIds.Count == 0)
        {
            return false;
        }

        return await db.TalkParts.AsNoTracking()
            .AnyAsync(part => part.Kind == TalkPartKind.Weather
                && part.Purpose == "WeatherReport"
                && part.AnnouncementId != null
                && airedIds.Contains(part.AnnouncementId.Value), ct);
    }

    private async Task<Announcement> ProduceWeatherHandoffAsync(
        AnnouncementFactory factory,
        Moderator mainHost,
        Moderator weatherHost,
        string stationName,
        CancellationToken ct)
    {
        var text = $"Here is {weatherHost.Name} with the weather.";

        return await factory.ProduceDirectAsync(
            AnnouncementKind.StationId,
            TalkPartKind.WeatherHandoff,
            TalkBreakPriority.Scheduled,
            mainHost,
            text,
            "WeatherHandoff",
            ct,
            expiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15),
            desiredDurationSeconds: 5,
            wordBudget: 12);
    }

    private async Task<Moderator> ResolveModeratorForAnnouncementAsync(
        Announcement announcement,
        Moderator fallback,
        CancellationToken ct)
    {
        if (announcement.ModeratorId == fallback.Id)
        {
            return fallback;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Moderators.AsNoTracking()
            .FirstOrDefaultAsync(moderator => moderator.Id == announcement.ModeratorId, ct)
            ?? fallback;
    }

    private async Task EnqueueHostChangeAsync(
        IServiceScope scope, Moderator newHost, int previousModeratorId, string stationName, CancellationToken ct)
    {
        try
        {
            string previousName;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                previousName = (await db.Moderators.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == previousModeratorId, ct))?.Name ?? "your previous host";
            }

            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SyncProductionBudget);

            var handover = await factory.ProduceAsync(
                AnnouncementKind.HostChange,
                newHost,
                relatedTrack: null,
                facts: $"You are taking over the show from {previousName}. Thank them, introduce yourself briefly and welcome the listeners to your part of the day.",
                stationName,
                budget.Token);

            playoutQueue.Enqueue(ToPlayoutItem(handover, newHost));
            logger.LogInformation("Host change on air: {NewHost} takes over from #{PreviousId}", newHost.Name, previousModeratorId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Host-change announcement failed; continuing without it");
        }
    }

    private async Task EnqueueFillerTalkAsync(
        IServiceScope scope, Moderator moderator, string stationName, CancellationToken ct)
    {
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SyncProductionBudget);

            var filler = await factory.ProduceAsync(
                AnnouncementKind.StationId,
                moderator,
                relatedTrack: null,
                facts: "We are warming up the studio — the AI is composing the very first songs right now. Stay tuned!",
                stationName,
                budget.Token);

            playoutQueue.Enqueue(ToPlayoutItem(filler, moderator));
            logger.LogInformation("Cold start: enqueued filler talk");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cold-start filler production failed (LLM/TTS not ready yet?)");
        }
    }

    private static PlayoutItem ToPlayoutItem(Announcement announcement, Moderator moderator) => new(
        PlayoutItemType.Announcement,
        announcement.Id,
        announcement.FilePath,
        $"{announcement.Kind} — {moderator.Name}",
        announcement.DurationSeconds,
        moderator.Id);
}
