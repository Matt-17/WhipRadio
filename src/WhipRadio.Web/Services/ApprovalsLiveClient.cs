using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Streams the Boss approval queue. The orchestrator raises <c>ApprovalsChanged</c>
/// (no payload) whenever an approval is created or resolved; this client refetches
/// the current list. Shared by the Verbs page and the chat approvals strip.
/// </summary>
public class ApprovalsLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ApprovalsLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public IReadOnlyList<PendingApprovalDto> Approvals { get; private set; } = [];

    public IReadOnlyList<PendingApprovalDto> Pending
        => Approvals.Where(a => string.Equals(a.Status, "Pending", StringComparison.OrdinalIgnoreCase)).ToList();

    public event Action? Changed;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On("ApprovalsChanged", async () => await RefreshSnapshotAsync());

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    public async Task RefreshSnapshotAsync()
    {
        Approvals = await api.GetApprovalsAsync();
        Changed?.Invoke();
    }
}
