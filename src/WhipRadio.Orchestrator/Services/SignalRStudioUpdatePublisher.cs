using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Abstractions;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public class SignalRStudioUpdatePublisher(
    IHubContext<RadioHub> hub,
    ILogger<SignalRStudioUpdatePublisher> logger) : IStudioUpdatePublisher
{
    public async Task PublishStudiosChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("StudiosChanged", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "SignalR studio publish failed");
        }
    }
}
