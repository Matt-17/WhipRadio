using Microsoft.AspNetCore.SignalR;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public interface IProductionUpdatePublisher
{
    Task PublishNewsChangedAsync(CancellationToken ct = default);

    Task PublishWeatherChangedAsync(CancellationToken ct = default);

    Task PublishConversationsChangedAsync(CancellationToken ct = default);

    Task PublishArchiveChangedAsync(CancellationToken ct = default);
}

public sealed class SignalRProductionUpdatePublisher(
    IHubContext<RadioHub> hub,
    ILogger<SignalRProductionUpdatePublisher> logger) : IProductionUpdatePublisher
{
    public async Task PublishNewsChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("NewsProductionChanged", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR news production publish failed");
        }
    }

    public async Task PublishWeatherChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("WeatherProductionChanged", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR weather production publish failed");
        }
    }

    public async Task PublishConversationsChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ConversationsProductionChanged", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR conversations production publish failed");
        }
    }

    public async Task PublishArchiveChangedAsync(CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ArchiveChanged", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR archive publish failed");
        }
    }
}
