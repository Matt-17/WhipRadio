using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

public class ArtistFeedLiveClient(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ArtistFeedLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public event Action<ArtistPostDto>? PostAdded;

    protected override void RegisterHandlers(HubConnection connection)
        => connection.On<ArtistPostDto>("ArtistPostAdded", post => PostAdded?.Invoke(post));
}
