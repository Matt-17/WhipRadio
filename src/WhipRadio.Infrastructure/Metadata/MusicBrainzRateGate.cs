using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace WhipRadio.Infrastructure.Metadata;

/// <summary>
/// Process-wide courtesy gate for the MusicBrainz web service: request starts
/// are spaced at least the configured interval apart (MusicBrainz policy is
/// one request per second for anonymous clients). Automatic HTTP retries are
/// removed from the client precisely so this gate is the only pacing.
/// </summary>
public sealed class MusicBrainzRateGate(IOptions<MusicMetadataOptions> options, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRequestTimestamp;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(0, options.Value.MusicBrainzMinRequestIntervalMs));
            var last = Interlocked.Read(ref _lastRequestTimestamp);
            if (last != 0)
            {
                var elapsed = timeProvider.GetElapsedTime(last);
                if (elapsed < interval)
                {
                    await Task.Delay(interval - elapsed, timeProvider, ct);
                }
            }

            Interlocked.Exchange(ref _lastRequestTimestamp, timeProvider.GetTimestamp());
        }
        finally
        {
            _gate.Release();
        }
    }
}
