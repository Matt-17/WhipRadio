using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Default the SQLite path under the configured data root unless explicitly set.
if (string.IsNullOrEmpty(builder.Configuration.GetConnectionString("radio")))
{
    var dataRoot = builder.Configuration["Radio:DataRoot"]
        ?? (Directory.Exists("/data") ? "/data" : Path.Combine(Directory.GetCurrentDirectory(), "data"));
    builder.Configuration["ConnectionStrings:radio"] = $"Data Source={Path.Combine(dataRoot, "db", "radio.db")}";
}

builder.Services.Configure<RadioOptions>(builder.Configuration.GetSection(RadioOptions.SectionName));
builder.Services.Configure<IcecastOptions>(builder.Configuration.GetSection(IcecastOptions.SectionName));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection(StreamOptions.SectionName));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection(MusicOptions.SectionName));

builder.Services.AddRadioPersistence(builder.Configuration);
builder.Services.AddRadioHttpClients(builder.Configuration);
builder.Services.AddHttpClient("icecast-admin", client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddScoped<RadioDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<RadioDbContext>>().CreateDbContext());
builder.Services.AddScoped<MusicCopywriter>();
builder.Services.AddScoped<AnnouncementFactory>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<QueueStateTracker>();
builder.Services.AddSingleton<ChannelPlayoutQueue>();
builder.Services.AddSingleton<IPlayoutQueue>(sp => new TrackedPlayoutQueue(
    sp.GetRequiredService<ChannelPlayoutQueue>(),
    sp.GetRequiredService<QueueStateTracker>()));
builder.Services.AddSingleton<INowPlayingState, NowPlayingState>();
builder.Services.AddSingleton<IPlaybackReporter, PlaybackReporter>();
builder.Services.AddSingleton<VoiceCatalogService>();
builder.Services.AddSingleton<GreetingState>();
builder.Services.AddSingleton<DirectorControl>();
builder.Services.AddSingleton<HostLanguageAligner>();

builder.Services.AddSignalR();

var logBuffer = new InMemoryLogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new BufferLoggerProvider(logBuffer));

builder.Services.AddHostedService<PlayoutService>();
builder.Services.AddHostedService<ShowRunnerService>();
builder.Services.AddHostedService<MusicProductionService>();
builder.Services.AddHostedService<AnnouncementProductionService>();
builder.Services.AddHostedService<ProgramDirectorService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapRadioApi();
app.MapGreetingsApi();
app.MapHub<RadioHub>("/hubs/radio");

// Migrate + seed before the pipelines start consuming the database.
await using (var db = await app.Services
    .GetRequiredService<IDbContextFactory<RadioDbContext>>()
    .CreateDbContextAsync())
{
    await DbInitializer.EnsureSeededAsync(db);
}

// The station language is the main language: hosts in another language are aligned.
await app.Services.GetRequiredService<HostLanguageAligner>().AlignAsync();

app.Run();
