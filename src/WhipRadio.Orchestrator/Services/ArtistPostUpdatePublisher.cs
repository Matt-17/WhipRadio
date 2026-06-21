using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Api;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public interface IArtistPostUpdatePublisher
{
    Task PublishPostAddedAsync(ArtistPostDto post, CancellationToken ct = default);
}

public sealed class SignalRArtistPostUpdatePublisher(
    IHubContext<RadioHub> hub,
    ILogger<SignalRArtistPostUpdatePublisher> logger) : IArtistPostUpdatePublisher
{
    public async Task PublishPostAddedAsync(ArtistPostDto post, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ArtistPostAdded", post, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish ArtistPostAdded for post {PostId}", post.Id);
        }
    }
}
