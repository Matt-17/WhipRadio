using Microsoft.AspNetCore.SignalR.Client;

namespace WhipRadio.Web.Services;

/// <summary>
/// Shared lifecycle for the per-circuit SignalR live clients: one guarded start,
/// connection to the single <c>/hubs/radio</c> hub, automatic reconnect plus a
/// manual keep-knocking loop for long orchestrator restarts, and disposal.
/// Derived clients register their hub handlers and provide the snapshot refresh.
/// </summary>
public abstract class LiveClientBase(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private bool _started;
    private volatile bool _disposed;

    protected ILogger Logger { get; } = logger;

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

            await OnStartingAsync();

            var connection = new HubConnectionBuilder()
                .WithUrl($"{OrchestratorEndpoint.Resolve(configuration, environment)}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();
            _connection = connection;

            RegisterHandlers(connection);

            connection.Reconnected += async _ => await RefreshCoreAsync();

            // WithAutomaticReconnect gives up after ~30 s. Orchestrator restarts
            // (AI model loads) can take minutes — keep knocking until the studio
            // answers, or every open page stays frozen on stale data forever.
            connection.Closed += async _ =>
            {
                while (!_disposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    if (_connection is null)
                    {
                        return;
                    }

                    try
                    {
                        await connection.StartAsync();
                        await OnManualReconnectedAsync();
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        // orchestrator still rebooting — try again
                    }
                }
            };

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await connection.StartAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SignalR connect failed for {Client}; falling back to snapshot only", GetType().Name);
            }

            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wire the client's hub message handlers.</summary>
    protected abstract void RegisterHandlers(HubConnection connection);

    /// <summary>Fetch the HTTP snapshot and notify subscribers. Runs on automatic
    /// reconnect and (by default) before connecting and after a manual reconnect.</summary>
    protected virtual Task RefreshCoreAsync() => Task.CompletedTask;

    /// <summary>Runs once before the connection is built (initial snapshot).</summary>
    protected virtual Task OnStartingAsync() => RefreshCoreAsync();

    /// <summary>Runs after the keep-knocking loop re-established the connection.</summary>
    protected virtual Task OnManualReconnectedAsync() => RefreshCoreAsync();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
