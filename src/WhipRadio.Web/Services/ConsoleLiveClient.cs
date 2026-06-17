using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit live console state: SignalR log push from the orchestrator with
/// an HTTP snapshot on connect/reconnect.
/// </summary>
public class ConsoleLiveClient(RadioApiClient api, IConfiguration configuration, ILogger<ConsoleLiveClient> logger)
    : IAsyncDisposable
{
    private const int MaxLines = 300;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private HubConnection? _connection;
    private List<ConsoleLineDto> _lines = [];
    private List<StudioDto> _studios = [];
    private bool _started;
    private bool _disposed;

    public IReadOnlyList<ConsoleLineDto> Lines
    {
        get
        {
            lock (_stateLock)
            {
                return _lines.ToList();
            }
        }
    }

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

            var baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? "http://orchestrator";

            _connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();

            _connection.On<ConsoleLineDto>("ConsoleLineAdded", AddLine);
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
                await _connection.StartAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR console connect failed; falling back to snapshot only");
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
        var snapshot = await api.GetConsoleAsync();
        var studios = await api.GetStudiosAsync();

        lock (_stateLock)
        {
            _lines = snapshot
                .Concat(_lines)
                .Distinct()
                .OrderByDescending(line => line.TimestampUtc)
                .Take(MaxLines)
                .ToList();
            _studios = studios;
            HasSnapshot = true;
        }

        Changed?.Invoke();
    }

    private void AddLine(ConsoleLineDto line)
    {
        lock (_stateLock)
        {
            if (_lines.Contains(line))
            {
                return;
            }

            _lines.Add(line);
            _lines = _lines
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(MaxLines)
                .ToList();
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
