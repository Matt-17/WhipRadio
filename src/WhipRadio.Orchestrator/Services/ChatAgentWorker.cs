using WhipRadio.Core.Entities;

namespace WhipRadio.Orchestrator.Services;

public sealed class ChatAgentWorker(
    ChatTurnQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatAgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (ChatTurnRequest request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                ChatAgentTurnService turns = scope.ServiceProvider.GetRequiredService<ChatAgentTurnService>();
                await turns.RunTurnAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat agent turn failed for channel {ChannelId}", request.ChannelId);
                try
                {
                    using IServiceScope scope = scopeFactory.CreateScope();
                    ChatService chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                    await chat.PostAsync(
                        request.ChannelId,
                        ChatSenderKind.System,
                        moderatorId: null,
                        "The agent could not answer because the writer room failed.",
                        actionsJson: null,
                        request.CorrelationId,
                        request.HopCount,
                        CancellationToken.None);
                }
                catch (Exception postEx)
                {
                    logger.LogWarning(postEx, "Failed to post chat turn failure message");
                }
            }
        }
    }
}
