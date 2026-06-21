using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Api;
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

public sealed record StationStatusInfo(StationStatus Status, string? Reason, DateTime? NextAttemptUtc)
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

    /// <summary>Update the state and fire-and-forget a hub push.</summary>
    void Set(StationStatus status, string? reason = null, DateTime? nextAttemptUtc = null);

    Task PublishAsync(CancellationToken ct = default);
}

public sealed class StationStatusReporter(
    IHubContext<RadioHub> hub,
    ILogger<StationStatusReporter> logger) : IStationStatusReporter
{
    private StationStatusInfo _current = StationStatusInfo.Online;
    private int _publishing;

    public StationStatusInfo Current => Volatile.Read(ref _current);

    public void Set(StationStatus status, string? reason = null, DateTime? nextAttemptUtc = null)
    {
        Volatile.Write(ref _current, new StationStatusInfo(status, reason, nextAttemptUtc));
        _ = PublishAsync();
    }

    public async Task PublishAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _publishing, 1) == 1)
        {
            return;
        }

        try
        {
            var info = Current;
            var dto = new StationStatusDto(info.Status.ToString(), info.Reason, info.NextAttemptUtc);
            await hub.Clients.All.SendAsync("StationStatusChanged", dto, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "SignalR station-status publish failed");
        }
        finally
        {
            Interlocked.Exchange(ref _publishing, 0);
        }
    }
}
