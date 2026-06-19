using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Studios;

public sealed class NoOpStudioUpdatePublisher : IStudioUpdatePublisher
{
    public Task PublishStudiosChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
}
