using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

WhipRadio.AppHost.DotEnv.Load();

var builder = DistributedApplication.CreateBuilder(args);
builder.Services.Configure<LoggerFilterOptions>(options =>
{
    options.Rules.Add(new LoggerFilterRule(
        "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider",
        categoryName: null,
        logLevel: LogLevel.None,
        filter: null));
});

// --- Parameters -------------------------------------------------------------
// Icecast passwords are secrets — never committed with a dev fallback. They
// come from the environment (real env var, or .env loaded by DotEnv.Load).
// Fail fast with a clear message instead of silently using a baked-in default.
var icecastSourcePassword = RequiredSecret("ICECAST_SOURCE_PASSWORD");
var icecastAdminPassword = RequiredSecret("ICECAST_ADMIN_PASSWORD");
var icecastRelayPassword =
    Environment.GetEnvironmentVariable("ICECAST_RELAY_PASSWORD") ?? icecastSourcePassword;

static string RequiredSecret(string key) =>
    Environment.GetEnvironmentVariable(key)
    ?? throw new InvalidOperationException(
        $"Required secret '{key}' is not set. Copy .env.example to .env and fill in the values, "
        + "or set the environment variable directly.");

// Shared data root: generated audio files. The .NET projects run as local
// processes in dev, so a plain folder under the repo root works on Windows.
// The relational store lives in Postgres (below), not under this folder.
var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data"));
// Aspire project references are resource declarations, not compile references, so
// Core's ServiceEndpointDefaults.WriterRoom is not available here — keep in sync.
var writerRoomEndpoint = builder.Configuration["Llm:Endpoint"] ?? "http://localhost:11434";

// --- PostgreSQL: relational store ---------------------------------------------
// Persistent container with a named data volume so the station's library, play
// history, and settings survive restarts. AddDatabase("radio") injects the
// connection string as ConnectionStrings__radio, which AddRadioPersistence reads.
var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("whipradio-pgdata");
var radioDb = postgres.AddDatabase("radio");

// --- Icecast streaming server -------------------------------------------------
// libretime/icecast generates icecast.xml from env vars; deploy/icecast/icecast.xml
// documents the equivalent static config for other images.
var icecast = builder.AddContainer("icecast", "libretime/icecast", "latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("ICECAST_SOURCE_PASSWORD", icecastSourcePassword)
    .WithEnvironment("ICECAST_ADMIN_PASSWORD", icecastAdminPassword)
    .WithEnvironment("ICECAST_RELAY_PASSWORD", icecastRelayPassword)
    .WithEnvironment("ICECAST_MAX_SOURCES", "2")
    .WithHttpEndpoint(port: 8000, targetPort: 8000, name: "http");
var icecastEndpoint = icecast.GetEndpoint("http");

// --- Studios -------------------------------------------------------------------
// Writer Room/Ollama, music AIs, TTS booths, and analysis are not managed by
// Aspire. They run as standalone services (start-studios.ps1) or online APIs,
// configured and restarted by the operator.

// --- Orchestrator: pipelines + playout -----------------------------------------
var orchestrator = builder.AddProject<Projects.WhipRadio_Orchestrator>("orchestrator")
    .WithHttpEndpoint(port: 5151, name: "http")
    .WithReference(radioDb)
    .WaitFor(radioDb)
    .WaitFor(icecast)
    .WithEnvironment("Llm__Endpoint", writerRoomEndpoint)
    .WithEnvironment("Radio__DataRoot", dataRoot)
    .WithEnvironment("Stream__Mount", "/radio.mp3")
    .WithEnvironment("Stream__Bitrate", "192k")
    .WithEnvironment("Icecast__SourcePassword", icecastSourcePassword)
    .WithEnvironment("Icecast__AdminPassword", icecastAdminPassword)
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["Icecast__Host"] = icecastEndpoint.Property(EndpointProperty.Host);
        context.EnvironmentVariables["Icecast__Port"] = icecastEndpoint.Property(EndpointProperty.Port);
    });

// --- Web app -------------------------------------------------------------------
builder.AddProject<Projects.WhipRadio_Web>("web")
    .WithReference(orchestrator)
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["Stream__PublicUrl"] =
            ReferenceExpression.Create($"http://{icecastEndpoint.Property(EndpointProperty.Host)}:{icecastEndpoint.Property(EndpointProperty.Port)}/radio.mp3");
    })
    .WithExternalHttpEndpoints();

builder.Build().Run();
