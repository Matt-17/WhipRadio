using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatNotificationBus(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatNotificationBus> logger) : INotificationBus
{
    public async Task PublishAsync(StationNotification notification, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ChatService chat = scope.ServiceProvider.GetRequiredService<ChatService>();
        Guid stationChannelId = await chat.GetStationChannelIdAsync(ct);
        string occurred = notification.OccurredAtUtc is { } at ? $" [{at:HH:mm} UTC]" : string.Empty;
        string text = $"{notification.Kind} from {notification.Source}{occurred}: {notification.Message}";
        try
        {
            await chat.PostAsync(
                stationChannelId,
                ChatSenderKind.System,
                moderatorId: null,
                text,
                actionsJson: null,
                correlationId: null,
                hopCount: 0,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to publish station notification {Kind} from {Source}", notification.Kind, notification.Source);
        }
    }
}
