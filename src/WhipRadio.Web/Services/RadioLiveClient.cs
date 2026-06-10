using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit live state: SignalR push from the orchestrator (now playing,
/// votes, queue) with an HTTP snapshot on connect. Components subscribe to
/// <see cref="Changed"/> for instant updates.
/// </summary>
public class RadioLiveClient(RadioApiClient api, IConfiguration configuration, ILogger<RadioLiveClient> logger) : IAsyncDisposable
{
    private HubConnection? _connection;
    private bool _started;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NowPlayingDto? NowPlaying { get; private set; }

    public IReadOnlyList<QueueItemDto> Queue { get; private set; } = [];

    public event Action? Changed;

    public async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            var baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? "http://orchestrator";

            _connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();

            _connection.On<NowPlayingDto?>("NowPlayingChanged", dto =>
            {
                NowPlaying = dto;
                Changed?.Invoke();
            });

            _connection.On<VoteResultDto>("VotesChanged", votes =>
            {
                if (NowPlaying?.ItemId == votes.TrackId)
                {
                    NowPlaying = NowPlaying with { UpVotes = votes.UpVotes, DownVotes = votes.DownVotes };
                    Changed?.Invoke();
                }
            });

            _connection.On<List<QueueItemDto>>("QueueChanged", queue =>
            {
                Queue = queue;
                Changed?.Invoke();
            });

            _connection.Reconnected += async _ => await RefreshSnapshotAsync();

            try
            {
                await _connection.StartAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR connect failed; falling back to snapshot only");
            }

            await RefreshSnapshotAsync();
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshSnapshotAsync()
    {
        NowPlaying = await api.GetNowPlayingAsync();
        Queue = await api.GetQueueAsync();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
