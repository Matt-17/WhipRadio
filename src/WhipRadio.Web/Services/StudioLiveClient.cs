using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Per-circuit live studio overview: SignalR invalidation with HTTP snapshots.</summary>
public class StudioLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<StudioLiveClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private HubConnection? _connection;
    private List<StudioDto> _studios = [];
    private bool _started;
    private bool _disposed;

    public IReadOnlyList<StudioDto> Studios
    {
        get
        {
            lock (_stateLock)
            {
                return _studios.ToList();
            }
        }
    }

    public bool HasSnapshot { get; private set; }

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

            await RefreshSnapshotAsync();

            var baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator");

            _connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();

            _connection.On("StudiosChanged", async () => await RefreshSnapshotAsync());
            _connection.Reconnected += async _ => await RefreshSnapshotAsync();

            _connection.Closed += async _ =>
            {
                while (!_disposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (_connection is null)
                        {
                            return;
                        }

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
                        // orchestrator still rebooting - try again
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
                logger.LogWarning(ex, "SignalR studio connect failed; falling back to snapshot only");
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
        List<StudioDto> studios = [];
        try
        {
            studios = await api.GetStudiosAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Studio snapshot failed; keeping previous studio state");
        }

        lock (_stateLock)
        {
            _studios = studios;
            HasSnapshot = true;
        }

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
