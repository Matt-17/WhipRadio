using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Per-circuit mixer snapshot: HTTP once on connect, SignalR payloads after that.</summary>
public class MixerLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<MixerLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public MixerOverviewDto? Snapshot { get; private set; }

    public event Action? Changed;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On<MixerOverviewDto>("MixerChanged", snapshot =>
        {
            Snapshot = snapshot;
            Changed?.Invoke();
        });

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    public async Task RefreshSnapshotAsync()
    {
        Snapshot = await api.GetMixerAsync();
        Changed?.Invoke();
    }
}
