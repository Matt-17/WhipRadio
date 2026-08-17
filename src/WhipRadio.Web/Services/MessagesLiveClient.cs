using Microsoft.AspNetCore.SignalR.Client;

namespace WhipRadio.Web.Services;

/// <summary>
/// Signal-only live client for the listener mailbag. The orchestrator raises
/// <c>ListenerMessagesChanged</c> whenever a listener message is submitted, queued,
/// or dismissed; the Messages page re-loads its current page in response. Replaces
/// the page's 10-second polling timer.
/// </summary>
public class MessagesLiveClient(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<MessagesLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public event Action? Changed;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On("ListenerMessagesChanged", () => Changed?.Invoke());

    protected override Task RefreshCoreAsync()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
