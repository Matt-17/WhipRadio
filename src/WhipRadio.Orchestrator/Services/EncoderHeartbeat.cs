namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Lightweight liveness signal for the encoder pump. <see cref="PlayoutService"/>
/// stamps <see cref="LastBeatUtc"/> every loop iteration; the encoder health check
/// flags the station unhealthy when the clock stalls (encoder dead/hung or in a
/// silent crash-loop). In-process, lock-free reads are fine: a torn 64-bit write
/// on x64 is not a correctness risk for a staleness probe.
/// </summary>
public sealed class EncoderHeartbeat
{
    private long _lastBeatTicks;

    public EncoderHeartbeat(TimeProvider timeProvider)
    {
        _lastBeatTicks = timeProvider.GetUtcNow().UtcDateTime.Ticks;
    }

    public DateTime LastBeatUtc
    {
        get => new(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _lastBeatTicks, value.Ticks);
    }
}
