using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Tts;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// Builds the protocol adapter for a concrete studio: same provider classes as
/// before, but pointed at the studio's URL (or the cloud API with its key)
/// instead of a fixed sidecar address.
/// </summary>
public class StudioProviderFactory(
    IHttpClientFactory httpClientFactory,
    AceStepPromptBuilder promptBuilder,
    IOptions<AceStepOptions> aceStepOptions,
    ILoggerFactory loggerFactory)
{
    public const string StudioClientName = "studio";

    public IMusicGenerationProvider CreateMusicProvider(Studio studio)
    {
        var provider = MusicBackends.Normalize(studio.Provider);
        var client = httpClientFactory.CreateClient(StudioClientName);

        if (provider == MusicBackends.ElevenLabs)
        {
            client.BaseAddress = new Uri("https://api.elevenlabs.io");
            return new ElevenLabsMusicGenerationProvider(
                client, studio.ApiKey ?? "", loggerFactory.CreateLogger<ElevenLabsMusicGenerationProvider>());
        }

        client.BaseAddress = new Uri(studio.Url);
        return provider == MusicBackends.AceStep
            ? new AceStepGenerationProvider(
                client, promptBuilder, aceStepOptions, loggerFactory.CreateLogger<AceStepGenerationProvider>())
            : new MusicGenGenerationProvider(client, loggerFactory.CreateLogger<MusicGenGenerationProvider>());
    }

    public ITtsEngine CreateTtsEngine(Studio booth)
    {
        var client = httpClientFactory.CreateClient(StudioClientName);

        if (string.Equals(booth.Provider, StudioProviders.ElevenLabs, StringComparison.OrdinalIgnoreCase))
        {
            client.BaseAddress = new Uri("https://api.elevenlabs.io");
            return new ElevenLabsTtsEngine(client, booth.ApiKey ?? "");
        }

        client.BaseAddress = new Uri(booth.Url);
        return new HttpTtsEngine(client);
    }
}
