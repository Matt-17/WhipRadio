using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The conductor: picks the next track for the current slot, ensures its intro
/// announcement exists (producing it synchronously within a 60 s budget when the
/// pool is empty), and feeds the playout queue strictly sequentially.
/// On cold start (no tracks yet) it enqueues filler talk instead.
/// </summary>
public class ShowRunnerService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    IPlayoutQueue playoutQueue,
    ILogger<ShowRunnerService> logger) : BackgroundService
{
    /// <summary>Plan.md M6.3: if the queue already holds ≥2 items, wait.</summary>
    public const int MaxQueueDepth = 2;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdStartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncProductionBudget = TimeSpan.FromSeconds(60);

    private readonly Queue<Guid> _recentlyEnqueued = new();
    private int _tracksSinceAnnouncement;

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
                logger.LogError(ex, "ShowRunner cycle failed; retrying");
                delay = IdleDelay;
            }

            await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<ITrackSelector>();

        var (slot, moderator) = await schedule.GetCurrentAsync(ct);
        var settings = await GetSettingsAsync(ct);
        var track = await PickTrackAsync(selector, slot, moderator, ct);

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
                await EnqueueFillerTalkAsync(scope, moderator, settings.StationName, ct);
                return ColdStartDelay;

            case ShowAction.EnqueueTrackWithIntro:
                var intro = await EnsureIntroAsync(scope, track!, moderator, settings.StationName, ct);
                if (intro is not null)
                {
                    playoutQueue.Enqueue(ToPlayoutItem(intro, moderator));
                    _tracksSinceAnnouncement = 0;
                }
                else
                {
                    _tracksSinceAnnouncement++;
                }

                EnqueueTrack(track!, moderator);
                return TimeSpan.Zero;

            case ShowAction.EnqueueTrackOnly:
                EnqueueTrack(track!, moderator);
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

    private async Task<Track?> PickTrackAsync(ITrackSelector selector, ScheduleSlot slot, Moderator moderator, CancellationToken ct)
    {
        Track? track = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            track = await selector.PickNextAsync(slot, moderator, ct);
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

        logger.LogInformation("Enqueued track \"{Title}\"", track.Title);
    }

    private async Task<Announcement?> EnsureIntroAsync(
        IServiceScope scope, Track track, Moderator moderator, string stationName, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var pooled = await db.Announcements
                .AsNoTracking()
                .Where(a => a.Kind == AnnouncementKind.SongIntro && a.RelatedTrackId == track.Id && !a.WasPlayed)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (pooled is not null)
            {
                return pooled;
            }
        }

        // Pool empty: produce synchronously within the 60 s budget; on failure skip the intro.
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SyncProductionBudget);
            return await factory.ProduceAsync(AnnouncementKind.SongIntro, moderator, track, null, stationName, budget.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not produce intro for \"{Title}\" in time; playing without it", track.Title);
            return null;
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
