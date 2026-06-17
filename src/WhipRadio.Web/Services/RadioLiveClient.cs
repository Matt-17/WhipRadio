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
    ILogger<RadioLiveClient> logger) : IAsyncDisposable
{
    private HubConnection? _connection;
    private bool _started;
    private bool _disposed;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NowPlayingDto? NowPlaying { get; private set; }

    public IReadOnlyList<QueueItemDto> Queue { get; private set; } = [];

    public event Action? Changed;

    public event Action? JinglesChanged;

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

            await RefreshSnapshotAsync();

            var baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator");

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

            _connection.On("JinglesChanged", () => JinglesChanged?.Invoke());

            _connection.Reconnected += async _ => await RefreshSnapshotAsync();

            // WithAutomaticReconnect gives up after ~30 s. Orchestrator restarts
            // (AI model loads) can take minutes — keep knocking until the studio
            // answers, or every open page stays frozen on stale data forever.
            _connection.Closed += async _ =>
            {
                while (!_disposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _connection.StartAsync();
                        await RefreshSnapshotAsync();
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        // studio still rebooting — try again
                    }
                }
            };

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _connection.StartAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR connect failed; falling back to snapshot only");
            }

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
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
