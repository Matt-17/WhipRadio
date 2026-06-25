using Microsoft.Extensions.Logging;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// Fair, priority- and affinity-aware admission gate for the (single) local GPU,
/// keyed by resource group (e.g. <c>gpu:local</c>). Writer Room, Voice Booth and
/// Recording studios all share one group, so only one job runs at a time. When the
/// group frees, the next job is chosen by:
/// <list type="number">
///   <item>highest priority (re-evaluated at selection time — a ramping news job
///   overtakes once its priority rises);</item>
///   <item>affinity to the model already resident (avoid a reload);</item>
///   <item>FIFO (lowest sequence number).</item>
/// </list>
/// There is no preemption: a running job always finishes; only waiting jobs are
/// ordered.
/// </summary>
public sealed class LocalGpuScheduler(ILogger<LocalGpuScheduler> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    /// <summary>
    /// Wait for this job's turn on <paramref name="group"/>. Completes with a lease the
    /// caller must dispose when done. <paramref name="affinityKey"/> identifies the model
    /// family (typically the studio kind); <paramref name="priorityNow"/> is evaluated
    /// each time the scheduler picks a winner.
    /// </summary>
    public Task<GpuLease> AcquireAsync(
        string group, string affinityKey, Func<int> priorityNow, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(affinityKey);
        ArgumentNullException.ThrowIfNull(priorityNow);

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<GpuLease>(ct);
        }

        Reservation newReservation;
        Reservation? winner;
        GpuLease? lease;
        lock (_gate)
        {
            var state = GetOrAddState(group);
            newReservation = new Reservation(
                affinityKey, priorityNow, Interlocked.Increment(ref _sequence), ct);
            state.Waiters.Add(newReservation);
            (winner, lease) = TrySelectLocked(group, state);
        }

        // Grant the winner (if any) and wire cancellation for whoever still waits — all
        // outside the lock so continuations never run under it.
        if (winner is not null)
        {
            winner.Grant(lease!);
        }

        if (!ReferenceEquals(winner, newReservation))
        {
            newReservation.RegisterCancellation(() => CancelWaiter(group, newReservation));
        }

        return newReservation.Task;
    }

    private void Release(string group, string affinityKey)
    {
        Reservation? next;
        GpuLease? lease;
        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var state))
            {
                return;
            }

            state.Held = false;
            state.LoadedAffinity = affinityKey;
            (next, lease) = TrySelectLocked(group, state);
        }

        next?.Grant(lease!);
    }

    private void CancelWaiter(string group, Reservation reservation)
    {
        // Removing a waiter never frees the holder, so there is nothing to pump.
        lock (_gate)
        {
            if (_groups.TryGetValue(group, out var state))
            {
                state.Waiters.Remove(reservation);
            }
        }

        reservation.Cancel();
    }

    private (Reservation?, GpuLease?) TrySelectLocked(string group, GroupState state)
    {
        if (state.Held || state.Waiters.Count == 0)
        {
            return (null, null);
        }

        var winner = PickWinner(state);
        state.Waiters.Remove(winner);
        state.Held = true;

        var lease = new GpuLease(this, group, winner.AffinityKey, state.LoadedAffinity);
        if (!string.Equals(state.LoadedAffinity, winner.AffinityKey, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "GPU {Group}: switching model {From} -> {To}",
                group, state.LoadedAffinity ?? "(none)", winner.AffinityKey);
        }

        return (winner, lease);
    }

    private static Reservation PickWinner(GroupState state)
    {
        Reservation? best = null;
        var bestPriority = int.MinValue;
        foreach (var waiter in state.Waiters)
        {
            var priority = waiter.EvaluatePriority();
            if (best is null || IsBetter(priority, waiter, bestPriority, best, state.LoadedAffinity))
            {
                best = waiter;
                bestPriority = priority;
            }
        }

        return best!;
    }

    private static bool IsBetter(
        int priority, Reservation candidate,
        int bestPriority, Reservation best,
        string? loadedAffinity)
    {
        if (priority != bestPriority)
        {
            return priority > bestPriority;
        }

        var candidateAffinity = Matches(candidate.AffinityKey, loadedAffinity);
        var bestAffinity = Matches(best.AffinityKey, loadedAffinity);
        if (candidateAffinity != bestAffinity)
        {
            return candidateAffinity;
        }

        return candidate.Sequence < best.Sequence;
    }

    private static bool Matches(string affinityKey, string? loadedAffinity)
        => loadedAffinity is not null
            && string.Equals(affinityKey, loadedAffinity, StringComparison.OrdinalIgnoreCase);

    private GroupState GetOrAddState(string group)
    {
        if (!_groups.TryGetValue(group, out var state))
        {
            state = new GroupState();
            _groups[group] = state;
        }

        return state;
    }

    private sealed class GroupState
    {
        public bool Held { get; set; }

        public string? LoadedAffinity { get; set; }

        public List<Reservation> Waiters { get; } = [];
    }

    private sealed class Reservation
    {
        private readonly TaskCompletionSource<GpuLease> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<int> _priorityNow;
        private readonly CancellationToken _ct;
        private CancellationTokenRegistration _registration;

        public Reservation(string affinityKey, Func<int> priorityNow, long sequence, CancellationToken ct)
        {
            AffinityKey = affinityKey;
            _priorityNow = priorityNow;
            Sequence = sequence;
            _ct = ct;
        }

        public string AffinityKey { get; }

        public long Sequence { get; }

        public Task<GpuLease> Task => _tcs.Task;

        public int EvaluatePriority() => _priorityNow();

        public void RegisterCancellation(Action onCancel)
            => _registration = _ct.Register(onCancel);

        public void Grant(GpuLease lease)
        {
            _registration.Dispose();
            _tcs.TrySetResult(lease);
        }

        public void Cancel()
        {
            _registration.Dispose();
            _tcs.TrySetCanceled(_ct);
        }
    }

    /// <summary>
    /// A held turn on the GPU. Dispose exactly once when the job is done; this frees the
    /// group, records this job's model as resident, and admits the next waiter.
    /// </summary>
    public sealed class GpuLease(
        LocalGpuScheduler scheduler, string group, string affinityKey, string? previousAffinity)
        : IAsyncDisposable
    {
        private int _released;

        /// <summary>The model family that was resident before this lease ran (null = none).
        /// When it differs from this job's affinity, the caller should unload the foreign
        /// model before running.</summary>
        public string? PreviousAffinity { get; } = previousAffinity;

        /// <summary>True when this job uses a different model than the one already resident.</summary>
        public bool ModelSwitch =>
            !string.Equals(PreviousAffinity, affinityKey, StringComparison.OrdinalIgnoreCase);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                scheduler.Release(group, affinityKey);
            }

            return ValueTask.CompletedTask;
        }
    }
}
