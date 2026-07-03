using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Per-circuit live studio overview: SignalR invalidation with HTTP snapshots.</summary>
public class StudioLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<StudioLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    private readonly object _stateLock = new();
    private List<StudioDto> _studios = [];
    private List<StudioPendingOperationDto> _pendingOperations = [];

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

    public IReadOnlyList<StudioPendingOperationDto> PendingOperations
    {
        get
        {
            lock (_stateLock)
            {
                return _pendingOperations.ToList();
            }
        }
    }

    public bool HasSnapshot { get; private set; }

    public event Action? Changed;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On("StudiosChanged", async () => await RefreshSnapshotAsync());

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    public async Task RefreshSnapshotAsync()
    {
        var overview = new StudioOverviewDto([], []);
        try
        {
            overview = await api.GetStudioOverviewAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Studio snapshot failed; keeping previous studio state");
        }

        lock (_stateLock)
        {
            _studios = overview.Studios.ToList();
            _pendingOperations = overview.PendingOperations.ToList();
            HasSnapshot = true;
        }

        Changed?.Invoke();
    }
}
