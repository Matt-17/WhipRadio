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
            return pending;
        }
    }
}
