namespace WhipRadio.Core.Abstractions;

public sealed record StationNotification(
    string Kind,
    string Source,
    string Message,
    DateTime? OccurredAtUtc = null);

public interface INotificationBus
{
    Task PublishAsync(StationNotification notification, CancellationToken ct = default);
}
