namespace WhipRadio.Orchestrator.Services;

/// <summary>Serializes expensive artist-profile LLM calls across UI and director triggers.</summary>
public sealed class ArtistCreationQueue
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
