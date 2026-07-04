using System.Threading.Channels;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Orchestrator.Services;

/// <summary>A queued agent turn. A null <paramref name="Responder"/> means the Program Director.</summary>
public sealed record ChatTurnRequest(
    Guid ChannelId,
    ChatParticipantRef? Responder,
    Guid TriggerMessageId,
    Guid CorrelationId,
    int HopCount);

public sealed class ChatTurnQueue(ILogger<ChatTurnQueue> logger)
{
    private readonly Channel<ChatTurnRequest> _queue = Channel.CreateBounded<ChatTurnRequest>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public IAsyncEnumerable<ChatTurnRequest> ReadAllAsync(CancellationToken ct)
        => _queue.Reader.ReadAllAsync(ct);

    public bool TryEnqueue(ChatTurnRequest request)
    {
        bool queued = _queue.Writer.TryWrite(request);
        if (!queued)
        {
            logger.LogWarning("Chat turn queue rejected turn for channel {ChannelId}", request.ChannelId);
        }

        return queued;
    }
}
