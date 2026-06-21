namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Pure, clock-driven policy that schedules encoder-restart backoff and trips a
/// crash-rate circuit breaker. Kept free of ffmpeg / DI / SignalR so the
/// behaviour is unit-testable with a controlled clock — the
/// <see cref="PlayoutService"/> owns an instance and feeds it real
/// <see cref="DateTime.UtcNow"/> values.
/// </summary>
/// <remarks>
/// Two signals share the same rolling crash window:
/// <list type="bullet">
/// <item><b>Backoff</b>: grows exponentially with the number of crashes still in
/// the window, capped at <c>maxBackoff</c>. A session that ran longer than
/// <c>successResetsAfter</c> before crashing clears the window, so an unrelated
/// late crash starts the backoff from the floor instead of inheriting a hot-loop.</item>
/// <item><b>Circuit breaker</b>: trips when the window holds
/// <c>crashThreshold</c> crashes. The caller parks the station and waits for an
/// operator to re-enable On Air before calling <see cref="Reset"/>.</item>
/// </list>
/// </remarks>
public sealed class EncoderResiliencePolicy
{
    private readonly TimeSpan _window;
    private readonly int _threshold;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeSpan _successResetsAfter;
    private readonly Queue<DateTime> _crashes = new();
    private DateTime _sessionStartUtc;

    public EncoderResiliencePolicy(
        TimeSpan window,
        int threshold,
        TimeSpan initialBackoff,
        TimeSpan maxBackoff,
        TimeSpan successResetsAfter,
        DateTime nowUtc)
    {
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Crash threshold must be at least 1.");
        }

        _window = window;
        _threshold = threshold;
        _initialBackoff = initialBackoff;
        _maxBackoff = maxBackoff;
        _successResetsAfter = successResetsAfter;
        _sessionStartUtc = nowUtc;
    }

    /// <summary>Crashes currently inside the rolling window (after pruning).</summary>
    public int CrashesInWindow => _crashes.Count;

    /// <summary>Stamp the start of a fresh encoder session.</summary>
    public void MarkSessionStart(DateTime nowUtc) => _sessionStartUtc = nowUtc;

    /// <summary>
    /// Record a crash at <paramref name="nowUtc"/>. Returns <c>true</c> when the
    /// circuit breaker should trip (window now holds <c>threshold</c> crashes).
    /// </summary>
    public bool RecordCrash(DateTime nowUtc)
    {
        // A session that survived long enough before crashing signals recovery,
        // not a hot-loop: clear the window so this crash is treated as a fresh
        // incident (small backoff, breaker not primed by stale crashes).
        if (nowUtc - _sessionStartUtc >= _successResetsAfter)
        {
            _crashes.Clear();
        }

        Prune(nowUtc);
        _crashes.Enqueue(nowUtc);
        return _crashes.Count >= _threshold;
    }

    /// <summary>
    /// Backoff before the next restart attempt, growing exponentially with the
    /// number of crashes currently in the window and capped at
    /// <c>maxBackoff</c>. Sequence with defaults: 5s → 10s → 20s → 40s → 60s.
    /// </summary>
    public TimeSpan NextBackoff()
    {
        var exponent = Math.Max(0, _crashes.Count - 1);
        var seconds = Math.Min(
            _initialBackoff.TotalSeconds * Math.Pow(2, exponent),
            _maxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Clear the crash window — called after the station is re-enabled from a parked state.</summary>
    public void Reset() => _crashes.Clear();

    private void Prune(DateTime nowUtc)
    {
        while (_crashes.Count > 0 && nowUtc - _crashes.Peek() > _window)
        {
            _crashes.Dequeue();
        }
    }
}
