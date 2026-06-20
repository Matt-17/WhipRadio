using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.News;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Prompting;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Infrastructure.Weather;

namespace WhipRadio.Infrastructure;

public static class HttpClientsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI clients. Text generation and TTS go through settings-driven
    /// routers (ollama/openai, sidecar/elevenlabs). Writer Room/Ollama is an
    /// operator-owned studio service; explicit endpoints and Aspire connection
    /// strings take precedence over the local default.
    /// </summary>
    public static IServiceCollection AddRadioHttpClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<WeatherOptions>(configuration.GetSection(WeatherOptions.SectionName));
        services.Configure<AceStepOptions>(configuration.GetSection(AceStepOptions.SectionName));
        services.AddSingleton<StationSettingsCache>();

        // The AI clients are long-running (model loads, CPU inference). Aspire's default
        // standard resilience handler (~10 s attempt timeout + retries) would cancel and
        // re-send these calls — every retry queues another full generation in the sidecar —
        // so it is removed; the production services own their retry loops.
        services.AddHttpClient(TextGenerationRouter.OllamaClientName, client =>
            {
                client.BaseAddress = ResolveEndpoint(configuration, "Llm:Endpoint", "ollama", "http://localhost:11434");
                client.Timeout = TimeSpan.FromMinutes(10); // small models on CPU can be slow
            })
            .RemoveAllResilienceHandlers()
            .HardenForLongRunningCalls();

        services.AddHttpClient(TextGenerationRouter.OpenAiClientName, client =>
            {
                client.BaseAddress = new Uri(configuration["OpenAi:Endpoint"] ?? "https://api.openai.com");
                client.Timeout = TimeSpan.FromMinutes(3);
            })
            .RemoveAllResilienceHandlers();

        services.AddScoped<ITextGenerationService, TextGenerationRouter>();

        services.AddHttpClient(TtsEngineRouter.ElevenLabsClientName, client =>
            {
                client.BaseAddress = new Uri(configuration["ElevenLabs:Endpoint"] ?? "https://api.elevenlabs.io");
                client.Timeout = TimeSpan.FromMinutes(3);
            })
            .RemoveAllResilienceHandlers();

        // Studios: music AIs and TTS booths are user-configured endpoints (DB),
        // not fixed sidecars. The "studio" client gets its BaseAddress per booking;
        // the probe client runs the connection test on the studios page.
        services.AddHttpClient(StudioProviderFactory.StudioClientName, client =>
                client.Timeout = Timeout.InfiniteTimeSpan) // providers own their generation timeouts
            .RemoveAllResilienceHandlers()
            .HardenForLongRunningCalls();

        services.AddHttpClient(StudioCoordinator.ProbeClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllResilienceHandlers();

        services.AddSingleton<AceStepPromptBuilder>();
        services.AddSingleton<OllamaModelMemoryManager>();
        services.AddSingleton<IStudioUpdatePublisher, NoOpStudioUpdatePublisher>();
        services.AddSingleton<StudioCoordinator>();
        services.AddSingleton<StudioHistoryRecorder>();
        services.AddSingleton<StudioProviderFactory>();
        services.AddSingleton<StudioDockerControl>();
        services.AddScoped<ITtsEngine, TtsEngineRouter>();
        services.AddScoped<IVoiceDesignClient, VoiceDesignClient>();
        services.AddScoped<IMusicGenerator, StudioMusicGenerator>();

        services.AddHttpClient<OpenMeteoWeatherSource>(client =>
        {
            client.BaseAddress = new Uri(configuration["Weather:Endpoint"] ?? "https://api.open-meteo.com");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IAnnouncementDataSource>(sp => sp.GetRequiredService<OpenMeteoWeatherSource>());
        services.AddScoped<IWeatherReportSource>(sp => sp.GetRequiredService<OpenMeteoWeatherSource>());

        services.AddHttpClient(RssNewsFeedReader.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WhipRadio/1.0 (+https://localhost)");
        }).RemoveAllResilienceHandlers();
        services.AddScoped<INewsFeedReader, RssNewsFeedReader>();
        services.AddScoped<INewsArticleExtractor, HtmlArticleExtractor>();

        // Mixer audio analysis sidecar (CPU-only; started by start-studios.ps1).
        services.AddHttpClient<IAudioAnalysisClient, HttpAudioAnalysisClient>(client =>
            {
                client.BaseAddress = new Uri(configuration["Analysis:Endpoint"] ?? "http://localhost:8301");
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .RemoveAllResilienceHandlers();

        services.AddScoped<IScriptWriter, ScriptWriter>();
        services.AddScoped<IVoiceDirector, VoiceDirector>();
        services.AddSingleton<ICharacterToolCallParser, CharacterToolCallParser>();
        services.AddSingleton<ICharacterToolCatalog, CharacterToolCatalog>();
        services.AddSingleton<ICharacterTool, AnnounceTool>();
        services.AddSingleton<ICharacterTool, PlayTool>();
        services.AddSingleton<ICharacterTool, MessageTool>();
        services.AddSingleton<ICharacterTool, StartTalkBreakTool>();
        services.AddSingleton<ICharacterTool, RememberTool>();
        services.AddSingleton<ICharacterTool, RequestBitTool>();
        services.AddSingleton<ICharacterTool, NoOpTool>();

        return services;
    }

    /// <summary>
    /// Hardens a long-running AI client against "I/O operation aborted" failures:
    /// a stable SocketsHttpHandler with generous pooled-connection lifetime and an
    /// infinite factory handler lifetime so in-flight calls never lose their handler.
    /// </summary>
    private static IHttpClientBuilder HardenForLongRunningCalls(this IHttpClientBuilder builder)
        => builder
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(30),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });

    private static Uri ResolveEndpoint(IConfiguration configuration, string configKey, string connectionName, string fallback)
    {
        var explicitEndpoint = configuration[configKey];
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return new Uri(explicitEndpoint);
        }

        // Aspire integrations inject "Endpoint=http://..." style connection strings.
        var connectionString = configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var endpoint = connectionString.Split(';')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                ?["Endpoint=".Length..] ?? connectionString;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return new Uri(fallback);
    }
}
