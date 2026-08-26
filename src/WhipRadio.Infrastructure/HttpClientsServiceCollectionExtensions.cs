using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Configuration;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.News;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Privacy;
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
        services.TryAddSingleton<OutgoingHttpRequestAudit>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, PrivacyAuditHttpMessageHandlerFilter>());

        // The AI clients are long-running (model loads, CPU inference). Aspire's default
        // standard resilience handler (~10 s attempt timeout + retries) would cancel and
        // re-send these calls — every retry queues another full generation in the sidecar —
        // so it is removed; the production services own their retry loops.
        services.AddHttpClient(TextGenerationRouter.OllamaClientName, client =>
            {
                client.BaseAddress = ResolveEndpoint(configuration, "Llm:Endpoint", "ollama", ServiceEndpointDefaults.WriterRoom);
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
        services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();

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

        services.AddHttpClient(StudioEndpointProber.ProbeClientName, client =>
                client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllResilienceHandlers();

        services.AddSingleton<AceStepPromptBuilder>();
        services.AddSingleton<OllamaModelMemoryManager>();
        services.AddSingleton<LocalGpuScheduler>();
        services.AddSingleton<IStudioUpdatePublisher, NoOpStudioUpdatePublisher>();
        services.AddSingleton<StudioBookingRegistry>();
        services.AddSingleton<StudioEndpointProber>();
        services.AddSingleton<StudioPendingOperationsTracker>();
        services.AddSingleton<StudioCoordinator>();
        services.AddSingleton<StudioHistoryRecorder>();
        services.AddSingleton<StudioProviderFactory>();
        services.AddSingleton<StudioDockerControl>();
        services.AddScoped<ITtsEngine, TtsEngineRouter>();
        services.AddSingleton<IVoiceDesignClient, VoiceDesignClient>();
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

        // Open music metadata (Phase 6a): keyless, CC0-friendly sources.
        // MusicBrainz drops the resilience handler — automatic retries would
        // violate its 1-req/s etiquette; the rate gate owns all pacing.
        services.Configure<Metadata.MusicMetadataOptions>(
            configuration.GetSection(Metadata.MusicMetadataOptions.SectionName));
        services.AddSingleton<Metadata.MusicBrainzRateGate>();
        services.AddHttpClient<Metadata.IMusicBrainzClient, Metadata.MusicBrainzClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Metadata.MusicMetadataOptions>>().Value;
                client.BaseAddress = new Uri(configuration["MusicMetadata:MusicBrainzEndpoint"] ?? options.MusicBrainzEndpoint);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            })
            .RemoveAllResilienceHandlers();

        services.AddHttpClient<Metadata.WikidataClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Metadata.MusicMetadataOptions>>().Value;
            client.BaseAddress = new Uri(options.WikidataEndpoint);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });
        services.AddScoped<Metadata.IWikidataClient>(sp => sp.GetRequiredService<Metadata.WikidataClient>());

        services.AddHttpClient<Metadata.IWikipediaClient, Metadata.WikipediaClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Metadata.MusicMetadataOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        services.AddScoped<Metadata.KnowledgeDigestWriter>();

        // Mixer audio analysis sidecar (CPU-only; started by start-studios.ps1).
        services.AddHttpClient<IAudioAnalysisClient, HttpAudioAnalysisClient>(client =>
            {
                client.BaseAddress = new Uri(configuration["Analysis:Endpoint"] ?? ServiceEndpointDefaults.Analysis);
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .RemoveAllResilienceHandlers();

        services.AddScoped<IAnnouncementWriter, AnnouncementWriter>();
        services.AddSingleton<ICharacterToolCallParser, CharacterToolCallParser>();
        services.AddSingleton<IChatReplyParser, ChatReplyParser>();
        services.AddSingleton<ICharacterToolCatalog, CharacterToolCatalog>();
        services.AddSingleton<ICharacterTool, MessageTool>();
        services.AddSingleton<ICharacterTool, AnnouncementTool>();
        services.AddSingleton<ICharacterTool, SearchMusicTool>();
        services.AddSingleton<ICharacterTool, PlanFormatTool>();
        services.AddSingleton<ICharacterTool, HireHostTool>();
        services.AddSingleton<ICharacterTool, AssignHostTool>();
        services.AddSingleton<ICharacterTool, StatusReportTool>();
        services.AddSingleton<ICharacterTool, InviteTool>();
        services.AddSingleton<ICharacterTool, RemoveFromChannelTool>();
        services.AddSingleton<ICharacterTool, MakeSongTool>();
        services.AddSingleton<ICharacterTool, BriefPodcastTool>();
        services.AddSingleton<ICharacterTool, LookupKnowledgeTool>();
        services.AddSingleton<ICharacterTool, SearchArtistTool>();
        services.AddSingleton<ICharacterTool, GetArtistProfileTool>();
        services.AddSingleton<ICharacterTool, QueueTrackTool>();
        services.AddSingleton<ICharacterTool, PlanTalkBreakTool>();
        services.AddSingleton<ICharacterTool, CreateTalkBitTool>();
        services.AddSingleton<ICharacterTool, RememberTool>();
        services.AddSingleton<ICharacterTool, ProduceNewsPackageTool>();
        services.AddSingleton<ICharacterTool, ProduceWeatherReportTool>();
        services.AddSingleton<ICharacterTool, CreateJingleTool>();
        services.AddSingleton<ICharacterTool, SetJingleActiveTool>();
        services.AddSingleton<ICharacterTool, SetNewsPresenterTool>();
        services.AddSingleton<ICharacterTool, SetWeatherPresenterTool>();
        services.AddSingleton<ICharacterTool, RetireTrackTool>();
        services.AddSingleton<ICharacterTool, PostArtistFeedTool>();
        services.AddSingleton<ICharacterTool, RequestSongFromArtistTool>();
        services.AddSingleton<ICharacterTool, RequestBossApprovalTool>();
        services.AddSingleton<ICharacterTool, RetireArtistTool>();
        services.AddSingleton<ICharacterTool, DeleteArtistTool>();
        services.AddSingleton<ICharacterTool, DeleteTrackTool>();
        services.AddSingleton<ICharacterTool, DeleteJingleTool>();
        services.AddSingleton<ICharacterTool, RedefineArtistProfileTool>();
        services.AddSingleton<ICharacterTool, CancelSongProductionTool>();
        services.AddSingleton<ICharacterTool, RemoveShowTool>();
        services.AddSingleton<ICharacterTool, FireHostTool>();
        services.AddSingleton<ICharacterTool, EmergencyAnnouncementTool>();
        services.AddSingleton<ICharacterTool, AnswerListenerMessageTool>();
        services.AddSingleton<ICharacterTool, ManageNewsFeedTool>();
        services.AddSingleton<ICharacterTool, SetNewsProductionSettingsTool>();
        services.AddSingleton<ICharacterTool, SetWeatherSettingsTool>();
        services.AddSingleton<ICharacterTool, SetStationSettingsTool>();
        services.AddSingleton<ICharacterTool, SetProductionSwitchTool>();
        services.AddSingleton<ICharacterTool, SetProviderSettingsTool>();
        services.AddSingleton<ICharacterTool, StudioStatusTool>();
        services.AddSingleton<ICharacterTool, ServerStatusTool>();
        services.AddSingleton<ICharacterTool, PrivacyReportTool>();
        services.AddSingleton<ICharacterTool, MediaCleanupPreviewTool>();
        services.AddSingleton<ICharacterTool, RunMediaCleanupTool>();

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
