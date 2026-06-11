using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Music;

/// <summary>
/// Books the first free recording studio for each generation — artists queue
/// for A studio, not a specific one. The request is adapted to whatever
/// protocol the acquired studio speaks (e.g. vocals off for MusicGen).
/// </summary>
public sealed class StudioMusicGenerator(
    StudioCoordinator coordinator,
    StudioProviderFactory factory,
    ILogger<StudioMusicGenerator> logger) : IMusicGenerator
{
    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMinutes(30);

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct)
    {
        // Pin the provider only when the caller insists; with fallback allowed,
        // any free studio will do and the request adapts to it.
        var requiredProvider = !request.AllowProviderFallback && !string.IsNullOrWhiteSpace(request.Provider)
            ? MusicBackends.Normalize(request.Provider)
            : null;

        var label = $"Recording for {request.ArtistName ?? request.Genre}";
        var deadline = DateTime.UtcNow + AcquireTimeout;

        Studio? studio = null;
        while (studio is null)
        {
            studio = await coordinator.TryAcquireAsync(StudioKind.Recording, requiredProvider, label, ct);
            if (studio is not null)
            {
                break;
            }

            if (!await coordinator.AnyActiveAsync(StudioKind.Recording, requiredProvider, ct))
            {
                throw new MusicBackendUnavailableException(requiredProvider ?? "recording studio");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new MusicBackendUnavailableException("all recording studios busy");
            }

            // All studios occupied — the artist waits in line.
            await Task.Delay(AcquireRetryDelay, ct);
        }

        var success = false;
        try
        {
            var effective = AdaptRequestToStudio(request, studio);
            var provider = factory.CreateMusicProvider(studio);
            var result = await provider.GenerateAsync(effective, ct);
            success = true;
            return result;
        }
        finally
        {
            await coordinator.ReleaseAsync(studio.Id, success, CancellationToken.None);
        }
    }

    public Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
        => coordinator.AnyActiveAsync(StudioKind.Recording, MusicBackends.Normalize(backend), ct);

    private MusicRequest AdaptRequestToStudio(MusicRequest request, Studio studio)
    {
        var provider = MusicBackends.Normalize(studio.Provider);
        if (provider == MusicBackends.MusicGen && request.WantVocals)
        {
            logger.LogInformation(
                "{Studio} speaks MusicGen — vocals dropped for this recording", studio.Name);
            return request with
            {
                Provider = provider,
                WantVocals = false,
                Lyrics = null,
                LyricsMode = LyricsMode.Instrumental,
            };
        }

        return request with { Provider = provider };
    }
}
