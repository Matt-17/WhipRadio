namespace WhipRadio.Core.Abstractions;

public interface IStudioUpdatePublisher
{
    Task PublishStudiosChangedAsync(CancellationToken ct = default);
}
