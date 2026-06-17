var builder = DistributedApplication.CreateBuilder(args);

// --- Parameters -------------------------------------------------------------
var icecastSourcePassword = builder.AddParameter("icecast-source-password", "hackme-dev");
var icecastAdminPassword = builder.AddParameter("icecast-admin-password", "hackme-admin");

// Shared data root: SQLite db + generated audio. The .NET projects run as local
// processes in dev, so a plain folder under the repo root works on Windows.
var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data"));
var writerRoomEndpoint = builder.Configuration["Llm:Endpoint"] ?? "http://localhost:11434";

// --- Icecast streaming server -------------------------------------------------
// libretime/icecast generates icecast.xml from env vars; deploy/icecast/icecast.xml
// documents the equivalent static config for other images.
var icecast = builder.AddContainer("icecast", "libretime/icecast", "latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEnvironment("ICECAST_SOURCE_PASSWORD", icecastSourcePassword)
    .WithEnvironment("ICECAST_ADMIN_PASSWORD", icecastAdminPassword)
    .WithEnvironment("ICECAST_RELAY_PASSWORD", "hackme-relay")
    .WithEnvironment("ICECAST_MAX_SOURCES", "2")
    .WithHttpEndpoint(port: 8000, targetPort: 8000, name: "http");
var icecastEndpoint = icecast.GetEndpoint("http");

// --- Studios -------------------------------------------------------------------
// Writer Room/Ollama, music AIs, TTS booths, and analysis are not managed by
// Aspire. They run as standalone services (start-studios.ps1) or online APIs,
// configured and restarted by the operator.

// --- Orchestrator: pipelines + playout -----------------------------------------
var orchestrator = builder.AddProject<Projects.WhipRadio_Orchestrator>("orchestrator")
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
