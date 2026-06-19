using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Per-circuit mixer snapshot: HTTP once on connect, SignalR payloads after that.</summary>
public class MixerLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<MixerLiveClient> logger) : IAsyncDisposable
{
    private HubConnection? _connection;
    private bool _started;
    private bool _disposed;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MixerOverviewDto? Snapshot { get; private set; }

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

            _connection.On<MixerOverviewDto>("MixerChanged", snapshot =>
            {
                Snapshot = snapshot;
                Changed?.Invoke();
            });

            _connection.Reconnected += async _ => await RefreshSnapshotAsync();
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
                logger.LogWarning(ex, "SignalR mixer connect failed; falling back to snapshot only");
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
        Snapshot = await api.GetMixerAsync();
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
