using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

// Note: the Npgsql legacy-timestamp mapping (DateTime -> `timestamp without time zone`)
// is configured by a module initializer in WhipRadio.Infrastructure
// (NpgsqlConfiguration), so it applies to both the app and `dotnet ef` tooling.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The Aspire AppHost injects ConnectionStrings__radio for the "radio" Postgres
// database; AddRadioPersistence fails fast if it is missing.

builder.Services.Configure<RadioOptions>(builder.Configuration.GetSection(RadioOptions.SectionName));
builder.Services.Configure<IcecastOptions>(builder.Configuration.GetSection(IcecastOptions.SectionName));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection(StreamOptions.SectionName));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection(MusicOptions.SectionName));

builder.Services.AddRadioPersistence(builder.Configuration);
builder.Services.AddRadioHttpClients(builder.Configuration);
builder.Services.AddHttpClient("icecast-admin", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("health-icecast");
builder.Services.AddHttpClient("health-ollama");

// Station-specific readiness probes. The Aspire defaults only register a "self"
// liveness check; these cover the dependencies that actually determine whether
// the station can serve audio and generate content.
builder.Services.AddHealthChecks()
    .AddCheck<IcecastHealthCheck>("icecast", tags: ["ready"])
    .AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"])
    .AddCheck<RadioDbHealthCheck>("db", tags: ["ready"])
    .AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"])
    .AddCheck<EncoderHeartbeatHealthCheck>("encoder", tags: ["ready"]);

builder.Services.AddScoped<RadioDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<RadioDbContext>>().CreateDbContext());
builder.Services.AddScoped<MusicCopywriter>();
builder.Services.AddScoped<ArtistSocialFeedService>();
builder.Services.AddScoped<ArtistCreationService>();
builder.Services.AddScoped<SpecialistHostCreationService>();
builder.Services.AddScoped<AnnouncementFactory>();
builder.Services.AddScoped<TalkBitRuntimeService>();
builder.Services.AddScoped<SegmentRenderer>();
builder.Services.AddScoped<JingleProductionService>();
builder.Services.AddScoped<MessageModerator>();
builder.Services.AddScoped<MediaAnalysisRecorder>();
builder.Services.AddScoped<ModeratorMemoryService>();
builder.Services.AddSingleton<ProductionGate>();
builder.Services.AddSingleton<ArtistCreationQueue>();
builder.Services.AddSingleton<ArtistMemberVoiceQueue>();
builder.Services.AddSingleton<ArtistVoiceReferenceResolver>();
builder.Services.AddSingleton<ArtistDeletionService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<QueueStateTracker>();
builder.Services.AddSingleton<PlayoutStateStore>();
builder.Services.AddSingleton<ChannelPlayoutQueue>();
builder.Services.AddSingleton<IPlayoutQueue>(sp => new TrackedPlayoutQueue(
    sp.GetRequiredService<ChannelPlayoutQueue>(),
    sp.GetRequiredService<QueueStateTracker>(),
    sp.GetRequiredService<PlayoutStateStore>()));
builder.Services.AddSingleton<INowPlayingState, NowPlayingState>();
builder.Services.AddSingleton<IPlaybackReporter, PlaybackReporter>();
builder.Services.AddSingleton<TrackDeletionService>();
builder.Services.AddSingleton<HostVoiceQueue>();
builder.Services.AddSingleton<GreetingState>();
builder.Services.AddSingleton<MusicProductionControl>();
builder.Services.AddSingleton<DirectorControl>();
builder.Services.AddSingleton<HostLanguageAligner>();
builder.Services.AddSingleton<ServerStatsCollector>();
builder.Services.AddSingleton<MediaCleanupService>();
builder.Services.AddSingleton<PrivacyReportService>();
builder.Services.AddSingleton<IPromptContextBuilder, PromptContextBuilder>();
builder.Services.AddSingleton<IStudioUpdatePublisher, SignalRStudioUpdatePublisher>();
builder.Services.AddSingleton<IProductionUpdatePublisher, SignalRProductionUpdatePublisher>();
builder.Services.AddSingleton<IArtistPostUpdatePublisher, SignalRArtistPostUpdatePublisher>();
builder.Services.AddSingleton<WhipRadio.Core.Audio.IMixPlanner>(
    _ => new WhipRadio.Core.Audio.MixPlanner(new WhipRadio.Core.Audio.SystemRandomSource()));
builder.Services.AddSingleton<MixerDiagnostics>();
builder.Services.AddSingleton<MixerOverviewService>();
builder.Services.AddSingleton<IMixerUpdatePublisher, MixerUpdatePublisher>();
builder.Services.AddSingleton<IPcmSampleReaderFactory, FfmpegPcmSampleReaderFactory>();
builder.Services.AddSingleton<TimedPlayoutInterruptService>();
builder.Services.AddSingleton<FfmpegProcessRegistry>();
builder.Services.AddSingleton<IFfmpegLauncher, ProcessFfmpegLauncher>();
builder.Services.AddSingleton<EncoderHeartbeat>();
builder.Services.AddSingleton<IStationStatusReporter, StationStatusReporter>();
builder.Services.AddSingleton<IcecastListenerProbe>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IcecastListenerProbe>());
builder.Services.AddSingleton<IStationMetrics, StationMetrics>();
builder.Services.AddSingleton<AudioMixerEngine>();
builder.Services.AddSingleton<PriorityTalkBreakDispatcher>();
builder.Services.AddSingleton<EmergencyFallbackTrackService>();

builder.Services.AddSignalR();

var logBuffer = new InMemoryLogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new BufferLoggerProvider(logBuffer));

builder.Services.AddHostedService<PlayoutRecoveryService>();
builder.Services.AddHostedService<PlayoutService>();
builder.Services.AddHostedService<ShowRunnerService>();
builder.Services.AddHostedService<ArtistMemberVoicePreparationService>();
builder.Services.AddHostedService<HostVoicePreparationService>();
builder.Services.AddHostedService<MusicProductionService>();
builder.Services.AddHostedService<AnnouncementProductionService>();
        builder.Services.AddSingleton<NewsFeedPollingService>();
        builder.Services.AddSingleton<ITopOfHourSegmentContributor, NewsSegmentContributor>();
        builder.Services.AddSingleton<ITopOfHourSegmentContributor, WeatherSegmentContributor>();
        builder.Services.AddSingleton<ITopOfHourSegmentContributor, ShowReturnSegmentContributor>();
        builder.Services.AddSingleton<NewsPackageProductionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NewsPackageProductionService>());
builder.Services.AddHostedService<TopOfHourPackageDispatcher>();
builder.Services.AddHostedService<ProgramDirectorService>();
builder.Services.AddHostedService<MessageModerationService>();
builder.Services.AddHostedService<NightlyModeratorMemoryDistillationService>();
builder.Services.AddHostedService<TalkBreakCleanupService>();
builder.Services.AddHostedService<AnalysisBackfillService>();
builder.Services.AddHostedService<ConsoleLogBroadcaster>();

var app = builder.Build();

// Fail fast if the Icecast source password is missing — without it ffmpeg's
// push is rejected and the encoder hot-loops. In dev it arrives via .env
// (loaded by the AppHost) as the Icecast__SourcePassword env var; for
// standalone runs set the env var or use dotnet user-secrets.
var icecastOpts = app.Services.GetRequiredService<IOptions<IcecastOptions>>().Value;
if (string.IsNullOrWhiteSpace(icecastOpts.SourcePassword))
{
    throw new InvalidOperationException(
        "Icecast:SourcePassword is not set. Set the Icecast__SourcePassword environment variable "
        + "(or copy .env.example to .env) before starting the station.");
}

app.MapDefaultEndpoints();
app.MapRadioApi();
app.MapGreetingsApi();
app.MapHub<RadioHub>("/hubs/radio");

// Kill ffmpeg orphans from a previous run BEFORE the new encoder starts —
// otherwise they fight for the Icecast mount (stale audio after restarts).
// Embedded here so it works for every launch path: VS, scripts, Aspire.
app.Services.GetRequiredService<FfmpegProcessRegistry>().KillOrphansFromPreviousRun();

// Migrate + seed before the pipelines start consuming the database.
await using (var db = await app.Services
    .GetRequiredService<IDbContextFactory<RadioDbContext>>()
    .CreateDbContextAsync())
{
    await DbInitializer.EnsureSeededAsync(db);
}

var writerRoomOptions = app.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
var writerRoomClient = app.Services
    .GetRequiredService<IHttpClientFactory>()
    .CreateClient(TextGenerationRouter.OllamaClientName);
app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("WriterRoom")
    .LogInformation(
        "Writer Room configured: provider Ollama, model {Model}, context {ContextSize}, endpoint {Endpoint}",
        writerRoomOptions.Model,
        writerRoomOptions.ContextSize,
        writerRoomClient.BaseAddress);

// The station language is the main language: hosts in another language are aligned.
await app.Services.GetRequiredService<HostLanguageAligner>().AlignAsync();

app.Run();
