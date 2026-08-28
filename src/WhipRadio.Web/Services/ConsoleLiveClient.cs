using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Web.Services.Api;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit live console state: SignalR log push from the orchestrator with
/// an HTTP snapshot on connect/reconnect.
/// </summary>
public class ConsoleLiveClient(
    StationApiClient api,
    StudiosApiClient studiosApi,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ConsoleLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    private const int MaxLines = 300;
    private readonly object _stateLock = new();
    private List<ConsoleLineDto> _lines = [];
    private List<StudioDto> _studios = [];

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

    protected override void RegisterHandlers(HubConnection connection)
    {
        connection.On<ConsoleLineDto>("ConsoleLineAdded", AddLine);
        connection.On("StudiosChanged", async () => await RefreshStudiosAsync());
    }

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    public async Task RefreshSnapshotAsync()
    {
        List<ConsoleLineDto> snapshot = [];
        List<StudioDto> studios = [];
        try
        {
            snapshot = await api.GetConsoleAsync();
            studios = await studiosApi.GetStudiosAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Console snapshot failed; showing an empty console snapshot");
        }

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

    private async Task RefreshStudiosAsync()
    {
        List<StudioDto> studios = [];
        try
        {
            studios = await studiosApi.GetStudiosAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Studio refresh failed after SignalR update");
            return;
        }

        lock (_stateLock)
        {
            _studios = studios;
        }

        Changed?.Invoke();
    }
}
