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
        await Task.WhenAny(current.Task, Task.Delay(delay, ct));
        ct.ThrowIfCancellationRequested();
        if (current.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _trigger, NewSource(), current);
        }
    }

    private static TaskCompletionSource NewSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
