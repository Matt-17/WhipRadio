using WhipRadio.Core.Abstractions;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Seeds the in-memory queue from the last persisted playout timeline.</summary>
public sealed class PlayoutRecoveryService(
    PlayoutStateStore stateStore,
    IPlayoutQueue queue,
    ILogger<PlayoutRecoveryService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plan = stateStore.BuildResumePlan();
        stateStore.ResetForRestore();

        var restored = 0;
        foreach (var item in plan.ItemsToEnqueue())
        {
            queue.Enqueue(item);
            restored++;
        }

        if (restored > 0)
        {
            logger.LogInformation(
                "Restored {Count} playout item(s) after restart ({Skipped} skipped by elapsed downtime)",
                restored,
                plan.SkippedItems.Count);
        }
        else if (plan.SkippedItems.Count > 0)
        {
            logger.LogInformation(
                "Skipped {Count} stale playout item(s) after restart; show runner will refill the queue",
                plan.SkippedItems.Count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
