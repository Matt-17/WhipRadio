namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Serializes expensive guest-profile LLM calls across UI and director triggers.
/// Separate from <see cref="ArtistCreationQueue"/> so a slow artist build never
/// blocks guest booking.
/// </summary>
public sealed class GuestCreationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
