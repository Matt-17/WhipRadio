using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Api;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

public sealed class ConsoleLogBroadcaster(
    InMemoryLogBuffer buffer,
    IHubContext<RadioHub> hub,
    ILogger<ConsoleLogBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in buffer.Broadcast.ReadAllAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.All.SendAsync(
                    "ConsoleLineAdded",
                    new ConsoleLineDto(
                        entry.TimestampUtc,
                        entry.Level,
                        entry.Category,
                        entry.Message,
                        entry.SourceKind,
                        entry.SourceName),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "SignalR console log publish failed");
            }
        }
    }
}
