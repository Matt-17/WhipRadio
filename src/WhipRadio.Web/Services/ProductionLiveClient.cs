using Microsoft.AspNetCore.SignalR.Client;

namespace WhipRadio.Web.Services;

/// <summary>SignalR invalidation for news and weather production pages.</summary>
public class ProductionLiveClient(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ProductionLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public event Action? NewsChanged;

    public event Action? WeatherChanged;

    public event Action? ConversationsChanged;

    public event Action? ArchiveChanged;

    protected override void RegisterHandlers(HubConnection connection)
    {
        connection.On("NewsProductionChanged", () => NewsChanged?.Invoke());
        connection.On("WeatherProductionChanged", () => WeatherChanged?.Invoke());
        connection.On("ConversationsProductionChanged", () => ConversationsChanged?.Invoke());
        connection.On("ArchiveChanged", () => ArchiveChanged?.Invoke());
    }

    // Invalidation-only client: no HTTP snapshot on start, but pages must reload
    // after a reconnect because pushes were missed while the connection was down.
    protected override Task OnStartingAsync() => Task.CompletedTask;

    protected override Task RefreshCoreAsync()
    {
        NewsChanged?.Invoke();
        WeatherChanged?.Invoke();
        ConversationsChanged?.Invoke();
        ArchiveChanged?.Invoke();
        return Task.CompletedTask;
    }
}
