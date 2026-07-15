using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

internal sealed class TestPlayoutQueue : IPlayoutQueue
{
    public List<PlayoutItem> Items { get; } = [];

    public void Enqueue(PlayoutItem item) => Items.Add(item);

    public void EnqueueFront(PlayoutItem item) => Items.Insert(0, item);

    public PlayoutItem? PeekNext() => Items.FirstOrDefault();

    public Task<PlayoutItem> DequeueAsync(CancellationToken ct) => throw new NotSupportedException();

    public int Count => Items.Count;
}

internal sealed class TestNotificationBus : INotificationBus
{
    public List<StationNotification> Published { get; } = [];

    public Task PublishAsync(StationNotification notification, CancellationToken ct = default)
    {
        Published.Add(notification);
        return Task.CompletedTask;
    }
}

internal sealed class TestProductionUpdatePublisher : IProductionUpdatePublisher
{
    public int NewsChanged { get; private set; }

    public int WeatherChanged { get; private set; }

    public Task PublishNewsChangedAsync(CancellationToken ct = default)
    {
        NewsChanged++;
        return Task.CompletedTask;
    }

    public Task PublishWeatherChangedAsync(CancellationToken ct = default)
    {
        WeatherChanged++;
        return Task.CompletedTask;
    }

    public Task PublishConversationsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task PublishArchiveChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class RecordingArtistPostPublisher : IArtistPostUpdatePublisher
{
    public List<ArtistPostDto> Posts { get; } = [];

    public Task PublishPostAddedAsync(ArtistPostDto post, CancellationToken ct = default)
    {
        Posts.Add(post);
        return Task.CompletedTask;
    }
}

internal sealed class TestHubContext : IHubContext<RadioHub>
{
    public IHubClients Clients { get; } = new HubClients();

    public IGroupManager Groups { get; } = new GroupManager();

    private sealed class HubClients : IHubClients
    {
        public IClientProxy All { get; } = new Proxy();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy();

        public IClientProxy Client(string connectionId) => new Proxy();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy();

        public IClientProxy Group(string groupName) => new Proxy();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy();

        public IClientProxy User(string userId) => new Proxy();

        public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy();
    }

    private sealed class Proxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class GroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
