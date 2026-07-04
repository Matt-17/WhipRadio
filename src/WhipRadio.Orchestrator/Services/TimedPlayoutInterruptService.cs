using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

public sealed record TimedPlayoutInterrupt(
    PlayoutItem Item,
    DateTime TargetUtc,
    double FadeOutSeconds,
    int GraceSeconds,
    int LateWindowSeconds);

/// <summary>
/// Hand-off channel between dispatchers (top-of-hour news, podcast episodes)
/// and the mixer: holds the pending timed interrupts and releases the earliest
/// due one inside its claim window. Multiple interrupts may be pending at once
/// (a news package and a podcast episode with different targets); duplicates
/// are keyed by (ItemId, TargetUtc).
/// </summary>
public sealed class TimedPlayoutInterruptService(ILogger<TimedPlayoutInterruptService> logger)
{
    private const int RecentlyConsumedCap = 4;

    private readonly object _lock = new();
    private readonly List<TimedPlayoutInterrupt> _pending = [];
    private readonly List<(TimedPlayoutInterrupt Interrupt, DateTime ConsumedAtUtc)> _recentlyConsumed = [];

    public void Schedule(TimedPlayoutInterrupt interrupt)
    {
        lock (_lock)
        {
            if (_pending.Any(pending => pending.Item.ItemId == interrupt.Item.ItemId
                && pending.TargetUtc == interrupt.TargetUtc))
            {
                return;
            }

            _pending.Add(interrupt);
        }

        logger.LogInformation(
            "Timed playout interrupt scheduled for {Target:u}: {Title} (fade {Fade:F1}s, grace {Grace}s, late window {LateWindow}s)",
            interrupt.TargetUtc,
            interrupt.Item.Title,
            interrupt.FadeOutSeconds,
            interrupt.GraceSeconds,
            interrupt.LateWindowSeconds);
    }

    public bool HasPending(Guid announcementId, DateTime targetUtc)
    {
        lock (_lock)
        {
            return _pending.Any(pending => pending.Item.ItemId == announcementId
                && pending.TargetUtc == targetUtc);
        }
    }

    public bool WasRecentlyConsumed(Guid announcementId, DateTime targetUtc, TimeSpan minimumDelay)
        => WasRecentlyConsumed(announcementId, targetUtc, minimumDelay, DateTime.UtcNow);

    public bool WasRecentlyConsumed(Guid announcementId, DateTime targetUtc, TimeSpan minimumDelay, DateTime utcNow)
    {
        if (minimumDelay <= TimeSpan.Zero)
        {
            return false;
        }

        lock (_lock)
        {
            return _recentlyConsumed.Any(entry => entry.Interrupt.Item.ItemId == announcementId
                && entry.Interrupt.TargetUtc == targetUtc
                && utcNow - entry.ConsumedAtUtc < minimumDelay);
        }
    }

    public TimedPlayoutInterrupt? TryConsume(DateTime utcNow)
    {
        lock (_lock)
        {
            // Drop interrupts that missed their late window before picking a winner.
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var candidate = _pending[i];
                if (utcNow > candidate.TargetUtc.AddSeconds(candidate.LateWindowSeconds))
                {
                    logger.LogWarning(
                        "Timed playout interrupt missed its late window: {Title} target {Target:u}",
                        candidate.Item.Title,
                        candidate.TargetUtc);
                    _pending.RemoveAt(i);
                }
            }

            var due = _pending
                .Where(candidate => TopOfHourScheduler.IsInsidePackageClaimWindow(
                    utcNow, candidate.TargetUtc, candidate.GraceSeconds, candidate.LateWindowSeconds))
                .OrderBy(candidate => candidate.TargetUtc)
                .FirstOrDefault();
            if (due is null)
            {
                return null;
            }

            _pending.Remove(due);
            _recentlyConsumed.Add((due, utcNow));
            while (_recentlyConsumed.Count > RecentlyConsumedCap)
            {
                _recentlyConsumed.RemoveAt(0);
            }

            return due;
        }
    }

    /// <summary>
    /// Clears every pending interrupt so the mixer won't play a stale package
    /// announcement. Prefer <see cref="Clear(Guid)"/> when only one item was
    /// recreated/failed — a full clear also drops other dispatchers' interrupts.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_pending.Count > 0 || _recentlyConsumed.Count > 0)
            {
                logger.LogInformation(
                    "Timed playout interrupts cleared ({Pending} pending, {Consumed} recently consumed)",
                    _pending.Count,
                    _recentlyConsumed.Count);
                _pending.Clear();
                _recentlyConsumed.Clear();
            }
        }
    }

    /// <summary>Clears only the interrupts (and consumed markers) for one item.</summary>
    public void Clear(Guid itemId)
    {
        lock (_lock)
        {
            var removed = _pending.RemoveAll(pending => pending.Item.ItemId == itemId)
                + _recentlyConsumed.RemoveAll(entry => entry.Interrupt.Item.ItemId == itemId);
            if (removed > 0)
            {
                logger.LogInformation("Timed playout interrupt cleared for item {ItemId}", itemId);
            }
        }
    }
}
