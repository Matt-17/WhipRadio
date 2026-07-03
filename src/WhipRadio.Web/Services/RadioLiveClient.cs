using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit live state: SignalR push from the orchestrator (now playing,
/// votes, queue) with an HTTP snapshot on connect. Components subscribe to
/// <see cref="Changed"/> for instant updates.
/// </summary>
public class RadioLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<RadioLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public NowPlayingDto? NowPlaying { get; private set; }

    public StationStatusDto? StationStatus { get; private set; }

    public MediaCleanupStatusDto? MediaCleanupStatus { get; private set; }

    public IReadOnlyList<QueueItemDto> Queue { get; private set; } = [];

    public event Action? Changed;

    /// <summary>
    /// Fires when the station transitions back to Online after an encoder crash
    /// or a studio restart. The live player re-tunes to the fresh Icecast edge so
    /// it stops draining pre-restart buffer that lags the now-playing card.
    /// </summary>
    public event Action? LiveStreamRestored;

    public event Action? JinglesChanged;

    public event Action? ScheduleChanged;

    protected override void RegisterHandlers(HubConnection connection)
    {
        connection.On<NowPlayingDto?>("NowPlayingChanged", dto =>
        {
            NowPlaying = dto;
            Changed?.Invoke();
        });

        connection.On<StationStatusDto>("StationStatusChanged", status =>
        {
            // Non-Online → Online means the encoder reattached to the mount
            // (crash recovery / re-enable): an in-flight player is draining
            // stale buffer, so push it to the live edge.
            var cameOnline = StationStatus is { } prev && !IsOnline(prev.Status) && IsOnline(status.Status);
            StationStatus = status;
            Changed?.Invoke();
            if (cameOnline)
            {
                LiveStreamRestored?.Invoke();
            }
        });

        connection.On<VoteResultDto>("VotesChanged", votes =>
        {
            if (NowPlaying?.ItemId == votes.TrackId)
            {
                NowPlaying = NowPlaying with { UpVotes = votes.UpVotes, DownVotes = votes.DownVotes };
                Changed?.Invoke();
            }
        });

        connection.On<List<QueueItemDto>>("QueueChanged", queue =>
        {
            Queue = queue;
            Changed?.Invoke();
        });

        connection.On("JinglesChanged", () => JinglesChanged?.Invoke());
        connection.On("ScheduleChanged", () => ScheduleChanged?.Invoke());

        connection.On<MediaCleanupStatusDto>("MediaCleanupChanged", status =>
        {
            MediaCleanupStatus = status;
            Changed?.Invoke();
        });
    }

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    protected override async Task OnManualReconnectedAsync()
    {
        await RefreshSnapshotAsync();
        // Auto-reconnect already gave up (~30 s) before Closed fired, so this
        // was a real studio outage — the orchestrator (and its ffmpeg encoder)
        // restarted and replaced the Icecast source. Re-tune any open player
        // off its now-stale buffer.
        if (IsOnline(StationStatus?.Status))
        {
            LiveStreamRestored?.Invoke();
        }
    }

    // Matches StationStatus.Online.ToString() pushed by the orchestrator's reporter.
    private static bool IsOnline(string? status) =>
        string.Equals(status, "Online", StringComparison.OrdinalIgnoreCase);

    public async Task RefreshSnapshotAsync()
    {
        NowPlaying = await api.GetNowPlayingAsync();
        Queue = await api.GetQueueAsync();
        StationStatus = await api.GetStationStatusAsync();
        MediaCleanupStatus = await api.GetMediaCleanupStatusAsync();
        Changed?.Invoke();
    }
}
