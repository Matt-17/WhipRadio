using System.Diagnostics;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// --- GPU auto-detection -------------------------------------------------------
// All AI workloads (LLM, TTS, music) use the GPU when one is available.
// Override with Gpu:Disabled=true. CPU remains the fallback everywhere.
var useGpu = !builder.Configuration.GetValue("Gpu:Disabled", false) && HostHasNvidiaGpu();

// --- Parameters -------------------------------------------------------------
var icecastSourcePassword = builder.AddParameter("icecast-source-password", "hackme-dev");
var icecastAdminPassword = builder.AddParameter("icecast-admin-password", "hackme-admin");

// Shared data root: SQLite db + generated audio. The .NET projects run as local
// processes in dev, so a plain folder under the repo root works on Windows.
var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data"));

// --- LLM: Ollama with gemma3:4b ----------------------------------------------
var ollama = builder.AddOllama("ollama")
    .WithDataVolume("ollama-models");
if (useGpu)
{
    ollama.WithGPUSupport();
}

var chatModel = ollama.AddModel("chat-model", "gemma3:4b");

// --- Icecast streaming server -------------------------------------------------
// libretime/icecast generates icecast.xml from env vars; deploy/icecast/icecast.xml
// documents the equivalent static config for other images.
var icecast = builder.AddContainer("icecast", "libretime/icecast", "latest")
    .WithEnvironment("ICECAST_SOURCE_PASSWORD", icecastSourcePassword)
    .WithEnvironment("ICECAST_ADMIN_PASSWORD", icecastAdminPassword)
    .WithEnvironment("ICECAST_RELAY_PASSWORD", "hackme-relay")
    .WithEnvironment("ICECAST_MAX_SOURCES", "2")
    .WithHttpEndpoint(port: 8000, targetPort: 8000, name: "http");
var icecastEndpoint = icecast.GetEndpoint("http");

// --- Python sidecars (model cache shared via hf-cache volume) -----------------
var torchIndex = useGpu ? "cu121" : "cpu";

var tts = builder.AddDockerfile("tts", "../../sidecars/tts")
    .WithBuildArg("TORCH_INDEX", torchIndex)
    .WithVolume("hf-cache", "/models")
    .WithHttpEndpoint(port: 8001, targetPort: 8001, name: "http");

var music = builder.AddDockerfile("music", "../../sidecars/music")
    .WithBuildArg("TORCH_INDEX", torchIndex)
    .WithVolume("hf-cache", "/models")
    .WithHttpEndpoint(port: 8002, targetPort: 8002, name: "http");

if (useGpu)
{
    tts.WithContainerRuntimeArgs("--gpus=all");
    music.WithContainerRuntimeArgs("--gpus=all");
}

// --- Orchestrator: pipelines + playout -----------------------------------------
var orchestrator = builder.AddProject<Projects.WhipRadio_Orchestrator>("orchestrator")
    .WithReference(ollama)
    .WithReference(tts.GetEndpoint("http"))
    .WithReference(music.GetEndpoint("http"))
    .WaitFor(icecast)
    .WithEnvironment("Radio__DataRoot", dataRoot)
    .WithEnvironment("Stream__Mount", "/radio.mp3")
    .WithEnvironment("Stream__Bitrate", "192k")
    .WithEnvironment("Icecast__SourcePassword", icecastSourcePassword)
    .WithEnvironment(context =>
    {
        context.EnvironmentVariables["Icecast__Host"] = icecastEndpoint.Property(EndpointProperty.Host);
        context.EnvironmentVariables["Icecast__Port"] = icecastEndpoint.Property(EndpointProperty.Port);
    });

// chat-model waits for the gemma pull; the orchestrator retries LLM calls, so we
// only soft-depend via the dashboard (no WaitFor: first pull can take minutes).
_ = chatModel;

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

static bool HostHasNvidiaGpu()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "-L")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(5000);
        return process.HasExited && process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}
