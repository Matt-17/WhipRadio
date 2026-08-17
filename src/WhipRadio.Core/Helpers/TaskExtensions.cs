using Microsoft.Extensions.Logging;

namespace WhipRadio.Core.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Observes the task so a faulted fire-and-forget task never raises
    /// <see cref="TaskScheduler.UnobservedTaskException"/>. Pass a logger to
    /// record faults at Debug level instead of swallowing them silently.
    /// </summary>
    public static void Forget(this Task task, ILogger? logger = null)
    {
        // Inspired by https://twitter.com/ben_a_adams/status/1045060828700037125
        // Only tasks that may fault (not completed) or are faulted need observing,
        // so fast-path for successfully completed and canceled tasks.
        if (!task.IsCompleted || task.IsFaulted)
        {
            _ = ForgetAwaited(task, logger);
        }

        // Allocate the async/await state machine only when needed.
        static async Task ForgetAwaited(Task task, ILogger? logger)
        {
            try
            {
                // No need to resume on the original SynchronizationContext.
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The caller opted out of observing the result. Log at Debug so a
                // fire-and-forget fault is never truly invisible when a logger is supplied.
                logger?.LogDebug(ex, "Fire-and-forget task faulted.");
            }
        }
    }

    /// <summary>
    /// Awaits <see cref="Task.Delay(TimeSpan, CancellationToken)"/> but never throws
    /// when the token is cancelled during the delay. Replaces the repeated
    /// <c>Task.Delay(...).ContinueWith(_ => { }, CancellationToken.None)</c> idiom in
    /// background service loops, whose <c>while (!token.IsCancellationRequested)</c>
    /// guard handles the actual shutdown. Called fluently:
    /// <c>await stoppingToken.DelayNoThrow(CycleDelay)</c>.
    /// </summary>
    public static async Task DelayNoThrow(this CancellationToken cancellationToken, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal shutdown path for the delay; swallow it so the
            // caller's loop condition decides whether to stop.
        }
    }
}
