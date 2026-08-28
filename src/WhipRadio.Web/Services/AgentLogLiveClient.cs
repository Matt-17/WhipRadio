using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

using WhipRadio.Web.Services.Api;

namespace WhipRadio.Web.Services;

public class AgentLogLiveClient(
    ChatApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AgentLogLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    private const int MaxEntries = 300;

    public IReadOnlyList<AgentLogEntryDto> Entries { get; private set; } = [];

    public event Action? Changed;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On<AgentLogEntryDto>("AgentActionLogged", entry =>
        {
            PrependEntry(entry);
            Changed?.Invoke();
        });

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

    public async Task RefreshSnapshotAsync()
    {
        Entries = await api.GetAgentLogAsync(take: MaxEntries);
        Changed?.Invoke();
    }

    private void PrependEntry(AgentLogEntryDto entry)
    {
        List<AgentLogEntryDto> updated = [entry, .. Entries.Where(existing => existing.Id != entry.Id)];
        if (updated.Count > MaxEntries)
        {
            updated.RemoveRange(MaxEntries, updated.Count - MaxEntries);
        }

        Entries = updated;
    }
}
