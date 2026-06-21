using Microsoft.AspNetCore.SignalR;
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

    public void Publish()
        => _ = PublishAsync(CancellationToken.None);

    public async Task PublishAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _publishing, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await overview.GetAsync(ct);
            await hub.Clients.All.SendAsync("MixerChanged", snapshot, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "SignalR mixer publish failed");
        }
        finally
        {
            Interlocked.Exchange(ref _publishing, 0);
        }
    }
}
