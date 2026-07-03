namespace WhipRadio.Orchestrator.Services;

/// <summary>Lets the admin page kick the program director immediately instead of
/// waiting for its next scheduled cycle.</summary>
public class DirectorControl
{
    private TaskCompletionSource _trigger = NewSource();

    public DateTime? LastRunUtc { get; private set; }

    public void TriggerRun() => _trigger.TrySetResult();

    public void MarkRun() => LastRunUtc = DateTime.UtcNow;

    /// <summary>Waits for the cycle delay OR an admin trigger, whichever first.</summary>
    public async Task WaitForNextCycleAsync(TimeSpan delay, CancellationToken ct)
    {
        var current = _trigger;
        // Linked CTS cancels the delay timer when the trigger wins, so frequent
        // admin triggers don't leave orphaned timers running to completion.
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, delayCts.Token);
        var finished = await Task.WhenAny(current.Task, delayTask);
        if (finished != delayTask)
        {
            delayCts.Cancel();
        }

        ct.ThrowIfCancellationRequested();
        if (current.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _trigger, NewSource(), current);
        }
    }

    private static TaskCompletionSource NewSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
