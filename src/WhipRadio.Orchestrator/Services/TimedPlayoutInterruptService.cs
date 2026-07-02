using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

public sealed record TimedPlayoutInterrupt(
    PlayoutItem Item,
    DateTime TargetUtc,
    double FadeOutSeconds,
    int GraceSeconds,
    int LateWindowSeconds);

public sealed class TimedPlayoutInterruptService(ILogger<TimedPlayoutInterruptService> logger)
{
    private readonly object _lock = new();
    private TimedPlayoutInterrupt? _pending;
    private TimedPlayoutInterrupt? _recentlyConsumed;
    private DateTime? _recentlyConsumedAtUtc;

    public void Schedule(TimedPlayoutInterrupt interrupt)
    {
        lock (_lock)
        {
            if (_pending is not null
                && _pending.Item.ItemId == interrupt.Item.ItemId
                && _pending.TargetUtc == interrupt.TargetUtc)
            {
                return;
            }

            _pending = interrupt;
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
            return _pending is { } pending
                && pending.Item.ItemId == announcementId
                && pending.TargetUtc == targetUtc;
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
            if (_recentlyConsumed is not { } consumed
                || consumed.Item.ItemId != announcementId
                || consumed.TargetUtc != targetUtc
                || _recentlyConsumedAtUtc is null)
            {
                return false;
            }

            return utcNow - _recentlyConsumedAtUtc.Value < minimumDelay;
        }
    }

    public TimedPlayoutInterrupt? TryConsume(DateTime utcNow)
    {
        lock (_lock)
        {
            if (_pending is null)
            {
                return null;
            }

            var latest = _pending.TargetUtc.AddSeconds(_pending.LateWindowSeconds);
            if (utcNow > latest)
            {
                logger.LogWarning(
                    "Timed playout interrupt missed its late window: {Title} target {Target:u}",
                    _pending.Item.Title,
                    _pending.TargetUtc);
                _pending = null;
                return null;
            }

            if (!TopOfHourScheduler.IsInsidePackageClaimWindow(
                utcNow,
                _pending.TargetUtc,
                _pending.GraceSeconds,
                _pending.LateWindowSeconds))
            {
                return null;
            }

            var pending = _pending;
            _pending = null;
            _recentlyConsumed = pending;
            _recentlyConsumedAtUtc = utcNow;
            return pending;
        }
    }

    /// <summary>
    /// Clears any pending interrupt so the mixer won't play a stale package
    /// announcement. Called when a package is recreated or failed.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_pending is not null || _recentlyConsumed is not null)
            {
                logger.LogInformation(
                    "Timed playout interrupt cleared for {Target:u}: {Title}",
                    (_pending ?? _recentlyConsumed)!.TargetUtc,
                    (_pending ?? _recentlyConsumed)!.Item.Title);
                _pending = null;
                _recentlyConsumed = null;
                _recentlyConsumedAtUtc = null;
            }
        }
    }
}
