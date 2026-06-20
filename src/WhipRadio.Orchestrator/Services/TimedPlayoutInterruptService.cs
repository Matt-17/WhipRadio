using WhipRadio.Core.Abstractions;

namespace WhipRadio.Orchestrator.Services;

public sealed record TimedPlayoutInterrupt(
    PlayoutItem Item,
    DateTime TargetUtc,
    double FadeOutSeconds,
    int GraceSeconds);

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
            "Timed playout interrupt scheduled for {Target:u}: {Title} (fade {Fade:F1}s, grace {Grace}s)",
            interrupt.TargetUtc,
            interrupt.Item.Title,
            interrupt.FadeOutSeconds,
            interrupt.GraceSeconds);
    }

    public TimedPlayoutInterrupt? TryConsume(DateTime utcNow)
    {
        lock (_lock)
        {
            if (_pending is null)
            {
                return null;
            }

            var latest = _pending.TargetUtc.AddSeconds(_pending.GraceSeconds);
            if (utcNow > latest)
            {
                logger.LogWarning(
                    "Timed playout interrupt missed its grace window: {Title} target {Target:u}",
                    _pending.Item.Title,
                    _pending.TargetUtc);
                _pending = null;
                return null;
            }

            if (utcNow < _pending.TargetUtc)
            {
                return null;
            }

            var pending = _pending;
            _pending = null;
            return pending;
        }
    }
}
