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
    ILogger<ShowRunnerService> logger) : BackgroundService
{
    /// <summary>If the queue already holds ≥2 items, wait.</summary>
    public const int MaxQueueDepth = 2;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdStartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncProductionBudget = TimeSpan.FromSeconds(90);

    private readonly Queue<Guid> _recentlyEnqueued = new();
    private int _tracksSinceAnnouncement;
    private int _previousModeratorId = -1;
    private Track? _lastEnqueuedTrack;

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

        var track = await PickTrackAsync(selector, context, ct);

        var action = ShowPlanner.Decide(new ShowPlannerInput(
            playoutQueue.Count,
            MaxQueueDepth,
            TrackAvailable: track is not null,
            _tracksSinceAnnouncement,
            settings.AnnouncementEveryNTracks));

        switch (action)
        {
            case ShowAction.Wait:
                return IdleDelay;

            case ShowAction.EnqueueFillerTalk:
                await EnqueueFillerTalkAsync(scope, context.Moderator, settings.StationName, ct);
                return ColdStartDelay;

            case ShowAction.EnqueueTrackWithIntro:
                var talk = await PickOrProduceTalkAsync(scope, track!, context, settings.StationName, ct);
                if (talk is not null)
                {
                    playoutQueue.Enqueue(ToPlayoutItem(talk, context.Moderator));
                    _tracksSinceAnnouncement = 0;
                }
                else
                {
                    _tracksSinceAnnouncement++;
                }

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
    /// Decides WHAT the host says before the next track: a pooled weather report,
    /// a personal note / banter from the pool, a back-announce of the previous
    /// track, or a classic intro (pooled or produced on the spot).
    /// </summary>
    private async Task<Announcement?> PickOrProduceTalkAsync(
        IServiceScope scope, Track nextTrack, ShowContext context, string stationName, CancellationToken ct)
    {
        var moderator = context.Moderator;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Pooled weather first — it ages quickly.
            var weather = await FirstUnplayedAsync(db, AnnouncementKind.Weather, moderator.Id, ct);
            if (weather is not null)
            {
                return weather;
            }

            // Occasionally a personal note or banter from the pool.
            if (Random.Shared.NextDouble() < 0.3)
            {
                var personal = await FirstUnplayedAsync(db, AnnouncementKind.PersonalNote, moderator.Id, ct)
                    ?? await FirstUnplayedAsync(db, AnnouncementKind.Banter, moderator.Id, ct);
                if (personal is not null)
                {
                    return personal;
                }
            }

            // Pooled intro for exactly this track.
            var pooledIntro = await db.Announcements.AsNoTracking()
                .Where(a => a.Kind == AnnouncementKind.SongIntro && a.RelatedTrackId == nextTrack.Id && !a.WasPlayed)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (pooledIntro is not null)
            {
                return pooledIntro;
            }
        }

        // Nothing pooled: produce synchronously. 25%: back-announce the previous track.
        var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SyncProductionBudget);

            if (_lastEnqueuedTrack is not null && Random.Shared.NextDouble() < 0.25)
            {
                return await factory.ProduceAsync(
                    AnnouncementKind.SongOutro, moderator, _lastEnqueuedTrack, null, stationName, budget.Token);
            }

            return await factory.ProduceAsync(
                AnnouncementKind.SongIntro, moderator, nextTrack, null, stationName, budget.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not produce talk for \"{Title}\" in time; playing without it", nextTrack.Title);
            return null;
        }
    }

    private async Task<Announcement?> FirstUnplayedAsync(
        RadioDbContext db, AnnouncementKind kind, int moderatorId, CancellationToken ct)
        => await db.Announcements.AsNoTracking()
            .Where(a => a.Kind == kind && a.ModeratorId == moderatorId && !a.WasPlayed && a.RelatedTrackId == null)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

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
