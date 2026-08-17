using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Api;
using WhipRadio.Core.Helpers;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Coarse encoder/stream health the operator sees on the console:
/// <list type="bullet">
/// <item><term>Online</term><description>encoder pumping, mount fed.</description></item>
/// <item><term>Reconnecting</term><description>encoder crashed; backing off before restart.</description></item>
/// <item><term>Offline</term><description>circuit breaker tripped — station parked until On Air is re-enabled.</description></item>
/// </list>
/// Distinct from <see cref="EncoderHeartbeat"/> (a liveness probe for health
/// checks) and from <c>PlayoutEnabled</c> (operator intent): this is the
/// derived state the UI lamp should reflect.
/// </summary>
public enum StationStatus
{
    Online,
    Reconnecting,
    Offline,
}

public sealed record StationStatusInfo(StationStatus Status, string? Reason, DateTime? NextAttemptUtc, bool PlayoutEnabled = true)
{
    public static readonly StationStatusInfo Online = new(StationStatus.Online, null, null);
}

/// <summary>
/// Holds the current <see cref="StationStatusInfo"/> and pushes it to connected
/// web clients via the <c>"StationStatusChanged"</c> RadioHub event. Interface
/// extracted so <see cref="PlayoutService"/> can be constructed in tests without
/// a SignalR hub.
/// </summary>
public interface IStationStatusReporter
{
    StationStatusInfo Current { get; }

    /// <summary>Update the encoder health and fire-and-forget a hub push. Preserves the current On Air intent.</summary>
    void Set(StationStatus status, string? reason = null, DateTime? nextAttemptUtc = null);

    /// <summary>Update the operator On Air intent (off air = playout disabled) and push it to the lamp.</summary>
    void SetPlayoutEnabled(bool enabled);

    Task PublishAsync(CancellationToken ct = default);
}

public sealed class StationStatusReporter(
    IHubContext<RadioHub> hub,
    ILogger<StationStatusReporter> logger) : IStationStatusReporter
{
    private StationStatusInfo _current = StationStatusInfo.Online;
    private int _publishing;
    private int _pending;

    public StationStatusInfo Current => Volatile.Read(ref _current);

    public void Set(StationStatus status, string? reason = null, DateTime? nextAttemptUtc = null)
    {
        Volatile.Write(ref _current, Current with { Status = status, Reason = reason, NextAttemptUtc = nextAttemptUtc });
        PublishAsync().Forget(logger);
    }

    public void SetPlayoutEnabled(bool enabled)
    {
        var prev = Current;
        if (prev.PlayoutEnabled == enabled)
        {
            return;
        }

        Volatile.Write(ref _current, prev with { PlayoutEnabled = enabled });
        PublishAsync().Forget(logger);
    }

    // Coalesces concurrent publishes without dropping the newest state: a Set that
    // lands while a publish is in flight bumps _pending, and the in-flight publisher
    // re-checks it after releasing the flag so the freshest snapshot always goes out.
    public async Task PublishAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pending);
        while (true)
        {
            if (Interlocked.Exchange(ref _publishing, 1) == 1)
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _pending, 0) != 0)
                {
                    var info = Current;
                    var dto = new StationStatusDto(info.Status.ToString(), info.Reason, info.NextAttemptUtc, info.PlayoutEnabled);
                    await hub.Clients.All.SendAsync("StationStatusChanged", dto, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "SignalR station-status publish failed");
            }
            finally
            {
                Volatile.Write(ref _publishing, 0);
            }

            if (Volatile.Read(ref _pending) == 0)
            {
                return;
            }
        }
    }
}
