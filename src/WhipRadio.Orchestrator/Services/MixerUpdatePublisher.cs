using Microsoft.AspNetCore.SignalR;
using WhipRadio.Core.Helpers;
using WhipRadio.Orchestrator.Api;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Fire-and-forget mixer state push. Interface extracted so the mixer
/// state machine can be tested without a SignalR hub / overview service.</summary>
public interface IMixerUpdatePublisher
{
    void Publish();

    Task PublishAsync(CancellationToken ct = default);
}

public class MixerUpdatePublisher(
    IHubContext<RadioHub> hub,
    MixerOverviewService overview,
    ILogger<MixerUpdatePublisher> logger) : IMixerUpdatePublisher
{
    private int _publishing;
    private int _pending;

    public void Publish()
        => PublishAsync(CancellationToken.None).Forget();

    // Coalesces concurrent publishes without dropping the newest state: a Publish that
    // lands while a push is in flight bumps _pending, and the in-flight publisher
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
                    var snapshot = await overview.GetAsync(ct);
                    await hub.Clients.All.SendAsync("MixerChanged", snapshot, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "SignalR mixer publish failed");
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
