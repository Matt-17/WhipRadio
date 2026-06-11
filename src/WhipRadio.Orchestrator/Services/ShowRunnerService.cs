using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The conductor: resolves the current show context (format → host → genre),
/// picks the next track and a fitting talk segment, and feeds the playout queue
/// strictly sequentially. Talks vary: intros, back-announces ("das war …"),
/// weather, personal notes — and a proper handover when the host changes.
/// </summary>
public class ShowRunnerService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    IPlayoutQueue playoutQueue,
    TimeProvider timeProvider,
    ILogger<ShowRunnerService> logger) : BackgroundService
{
    /// <summary>If the queue already holds ≥2 items, wait.</summary>
    public const int MaxQueueDepth = 2;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdStartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncProductionBudget = TimeSpan.FromSeconds(90);

    private readonly Queue<Guid> _recentlyEnqueued = new();
    private readonly HashSet<Guid> _frontPushedGreetingIds = [];
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

        using var scope = scopeFactory.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<ITrackSelector>();
        var context = await schedule.GetCurrentAsync(ct);

        // Host changed since the last cycle → proper on-air handover first.
        if (_previousModeratorId >= 0 && _previousModeratorId != context.Moderator.Id && playoutQueue.Count < MaxQueueDepth)
        {
            await EnqueueHostChangeAsync(scope, context.Moderator, _previousModeratorId, settings.StationName, ct);
        }

        _previousModeratorId = context.Moderator.Id;

        // Fresh greeting segments jump the whole queue: right after the current item.
        await FrontPushGreetingSegmentsAsync(context.Moderator, ct);

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

                var talks = await PlanGapTalksAsync(scope, track!, context, settings.StationName, ct);
                foreach (var talk in talks)
                {
                    playoutQueue.Enqueue(ToPlayoutItem(talk, context.Moderator));
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

    /// <summary>
    /// Pushes unplayed greeting segments to the FRONT of the playout queue so they
    /// air right after the current item — listeners shouldn't wait three songs to
    /// hear their name. The id set guards against double-enqueueing across cycles.
    /// </summary>
    private async Task FrontPushGreetingSegmentsAsync(Moderator moderator, CancellationToken ct)
    {
        List<Announcement> unplayed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            unplayed = await db.Announcements.AsNoTracking()
                .Where(a => a.Kind == AnnouncementKind.ListenerGreeting && !a.WasPlayed)
                .OrderByDescending(a => a.CreatedAt) // newest pushed first → oldest ends up frontmost
                .ToListAsync(ct);
        }

        _frontPushedGreetingIds.RemoveWhere(id => unplayed.All(a => a.Id != id)); // aired → forget

        foreach (var greeting in unplayed.Where(g => _frontPushedGreetingIds.Add(g.Id)))
        {
            playoutQueue.EnqueueFront(ToPlayoutItem(greeting, moderator));
            logger.LogInformation("Greeting segment {Id} jumps to the front of the queue", greeting.Id);
        }
    }

    /// <summary>Oldest queued request whose track has been produced — ready for a dedication.</summary>
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
        return await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
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

        _recentlyEnqueued.Enqueue(track.Id);
        while (_recentlyEnqueued.Count > 3)
        {
            _recentlyEnqueued.Dequeue();
        }

        _lastEnqueuedTrack = track;
        logger.LogInformation("Enqueued track \"{Title}\"", track.Title);
    }

    /// <summary>
    /// Plans the talk chain for the gap before the next track. Talks are produced
    /// FRESH for this gap — nothing stale from a pool. The hourly weather report
    /// comes first when due (listener greetings don't pass through here anymore;
    /// they jump straight to the queue front), then the host's mood decides 0–3
    /// free talks ("that was …", a coffee story, "up next …") with varying
    /// lengths, scaled by the host's and format's talkativeness.
    /// </summary>
    private async Task<List<Announcement>> PlanGapTalksAsync(
        IServiceScope scope, Track nextTrack, ShowContext context, string stationName, CancellationToken ct)
    {
        var moderator = context.Moderator;
        var talks = new List<Announcement>();

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Weather: once per hour, first talk slot after the full hour — and only
            // a freshly prepared report (≤ 30 min old), never a stale one.
            var localNow = timeProvider.GetLocalNow();
            if (WeatherScheduler.IsAirWindow(localNow.Minute) && !await WeatherAiredThisHourAsync(db, localNow, ct))
            {
                var freshCutoff = DateTime.UtcNow.AddMinutes(-30);
                var weather = await db.Announcements.AsNoTracking()
                    .Where(a => a.Kind == AnnouncementKind.Weather && !a.WasPlayed && a.CreatedAt >= freshCutoff)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (weather is not null)
                {
                    talks.Add(weather);
                }
            }
        }

        // The host's mood: 0–3 fresh talks, scaled by host + format talkativeness.
        var talkativeness = TalkPlanner.EffectiveTalkativeness(moderator.Talkativeness, context.Format?.Talkativeness);
        var freeTalkCount = TalkPlanner.PickGapTalkCount(Random.Shared, talks.Count > 0, talkativeness);

        var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
        var introDone = false;

        for (var i = 0; i < freeTalkCount; i++)
        {
            var kind = TalkPlanner.PickFreeTalkKind(
                Random.Shared,
                hasNextTrack: !introDone,
                hasPreviousTrack: _lastEnqueuedTrack is not null && i == 0); // "that was …" only opens a gap

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(SyncProductionBudget);

                var relatedTrack = kind switch
                {
                    AnnouncementKind.SongIntro => nextTrack,
                    AnnouncementKind.SongOutro => _lastEnqueuedTrack,
                    _ => null,
                };

                var talk = await factory.ProduceAsync(
                    kind, moderator, relatedTrack, null, stationName, budget.Token,
                    lengthHint: TalkPlanner.PickLengthHint(Random.Shared, talkativeness));
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

    private static async Task<bool> WeatherAiredThisHourAsync(
        RadioDbContext db, DateTimeOffset localNow, CancellationToken ct)
    {
        var hourStartUtc = localNow.UtcDateTime.AddMinutes(-localNow.Minute).AddSeconds(-localNow.Second);
        var airedIds = await db.PlayLog.AsNoTracking()
            .Where(e => e.PlayedAt >= hourStartUtc && e.ItemType == PlayoutItemType.Announcement)
            .Select(e => e.ItemId)
            .ToListAsync(ct);
        if (airedIds.Count == 0)
        {
            return false;
        }

        return await db.Announcements.AsNoTracking()
            .AnyAsync(a => airedIds.Contains(a.Id) && a.Kind == AnnouncementKind.Weather, ct);
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
