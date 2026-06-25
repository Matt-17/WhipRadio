namespace WhipRadio.Core.Playout;

/// <summary>
/// Scheduling priority levels for GPU jobs (text writing, voice recording, music
/// generation). Higher = more urgent. Integers, not an enum, so callers can blend
/// in time-based ramps (see <see cref="NewsAirtimeRamp"/>) without losing ordering.
/// </summary>
public static class GpuJobPriority
{
    public const int Bulk = 0;
    public const int Low = 10;
    public const int Normal = 20;
    public const int Medium = 40;
    public const int High = 60;
    public const int Highest = 80;
    public const int Emergency = 100;
}

/// <summary>
/// Ambient (AsyncLocal) GPU scheduling priority for the current production. A whole
/// production (e.g. a news package, an announcement) pushes one scope; every nested
/// LLM / TTS / music call inherits it without threading a parameter through every
/// signature. The scheduler re-invokes the captured delegate at each selection, so a
/// time-based ramp keeps updating while a job waits in the queue.
/// </summary>
public static class GpuPriorityContext
{
    private static readonly AsyncLocal<Func<int>?> Current = new();

    /// <summary>The active priority delegate, or a constant Normal default.</summary>
    public static Func<int> CurrentFunc => Current.Value ?? DefaultPriority;

    /// <summary>True when a caller has already pushed a scope (so a nested default should
    /// defer to it rather than override the production-wide priority, e.g. a news ramp).</summary>
    public static bool IsAmbientSet => Current.Value is not null;

    /// <summary>Push <paramref name="priority"/> only if no scope is active yet; otherwise a
    /// no-op scope that leaves the caller's priority in place.</summary>
    public static IDisposable PushIfUnset(int priority)
        => IsAmbientSet ? NoopScope.Instance : Push(priority);

    private static int DefaultPriority() => GpuJobPriority.Normal;

    /// <summary>Push a constant priority for the lifetime of the returned scope.</summary>
    public static IDisposable Push(int priority) => Push(() => priority);

    /// <summary>
    /// Push a dynamic priority for the lifetime of the returned scope. The delegate is
    /// evaluated each time the scheduler picks the next job, so it can ramp with time.
    /// </summary>
    public static IDisposable Push(Func<int> priority)
    {
        ArgumentNullException.ThrowIfNull(priority);
        var previous = Current.Value;
        Current.Value = priority;
        return new Scope(previous);
    }

    private sealed class Scope(Func<int>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
