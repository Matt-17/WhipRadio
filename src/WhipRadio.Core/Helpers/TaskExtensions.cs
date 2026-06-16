namespace WhipRadio.Core.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Observes the task so a faulted fire-and-forget task never raises
    /// <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    public static void Forget(this Task task)
    {
        // Inspired by https://twitter.com/ben_a_adams/status/1045060828700037125
        // Only tasks that may fault (not completed) or are faulted need observing,
        // so fast-path for successfully completed and canceled tasks.
        if (!task.IsCompleted || task.IsFaulted)
        {
            _ = ForgetAwaited(task);
        }

        // Allocate the async/await state machine only when needed.
        static async Task ForgetAwaited(Task task)
        {
            try
            {
                // No need to resume on the original SynchronizationContext.
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Intentionally swallowed — the caller opted out of observing the result.
            }
        }
    }
}
