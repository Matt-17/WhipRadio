using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Music;

public sealed class MusicGenerator : IMusicGenerator
{
    private readonly IReadOnlyDictionary<string, IMusicGenerationProvider> providers;
    private readonly Func<CancellationToken, Task<string?>> defaultProviderAccessor;
    private readonly ILogger<MusicGenerator> logger;

    public MusicGenerator(
        IEnumerable<IMusicGenerationProvider> providers,
        StationSettingsCache settingsCache,
        ILogger<MusicGenerator> logger)
        : this(providers, async ct => (await settingsCache.GetAsync(ct)).DefaultMusicProvider, logger)
    {
    }

    public MusicGenerator(
        IEnumerable<IMusicGenerationProvider> providers,
        Func<CancellationToken, Task<string?>> defaultProviderAccessor,
        ILogger<MusicGenerator> logger)
    {
        this.providers = providers.ToDictionary(p => MusicBackends.Normalize(p.Id), StringComparer.OrdinalIgnoreCase);
        this.defaultProviderAccessor = defaultProviderAccessor;
        this.logger = logger;
    }

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct)
    {
        var requestedProvider = string.IsNullOrWhiteSpace(request.Provider)
            ? await defaultProviderAccessor(ct)
            : request.Provider;
        var selectedId = NormalizeKnownProvider(requestedProvider);
        var provider = GetProvider(selectedId);

        if (!await provider.IsAvailableAsync(ct))
        {
            if (selectedId == MusicBackends.AceStep && request.AllowProviderFallback)
            {
                var fallback = GetProvider(MusicBackends.MusicGen);
                if (await fallback.IsAvailableAsync(ct))
                {
                    logger.LogWarning(
                        "Music provider {Provider} is unavailable before job creation; falling back to {Fallback}",
                        selectedId, MusicBackends.MusicGen);
                    return await fallback.GenerateAsync(request with
                    {
                        Provider = MusicBackends.MusicGen,
                        LyricsMode = LyricsMode.Instrumental,
                        Lyrics = null,
                        WantVocals = false,
                    }, ct);
                }
            }

            throw new MusicBackendUnavailableException(selectedId);
        }

        return await provider.GenerateAsync(request with { Provider = selectedId }, ct);
    }

    public async Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
    {
        var normalized = MusicBackends.Normalize(backend);
        return providers.TryGetValue(normalized, out var provider) && await provider.IsAvailableAsync(ct);
    }

    private IMusicGenerationProvider GetProvider(string providerId)
        => providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new MusicProviderValidationException(
                $"Unknown music provider '{providerId}'. Valid values are '{MusicBackends.MusicGen}' and '{MusicBackends.AceStep}'.");

    private static string NormalizeKnownProvider(string? provider)
    {
        var normalized = MusicBackends.Normalize(provider);
        if (!MusicBackends.IsKnown(normalized))
        {
            throw new MusicProviderValidationException(
                $"Unknown music provider '{provider}'. Valid values are '{MusicBackends.MusicGen}' and '{MusicBackends.AceStep}'.");
        }

        return normalized;
    }
}
