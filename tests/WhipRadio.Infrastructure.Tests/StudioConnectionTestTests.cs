using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tests;

/// <summary>
/// Characterization of the studios-page connection test and runtime probing:
/// protocol sniffing per studio kind, API-key validation, and the health-detail
/// extraction. No database involved — the DB factory throws if touched.
/// </summary>
[TestClass]
public class StudioConnectionTestTests
{
    // ---- API providers --------------------------------------------------------

    [TestMethod]
    public async Task Test_OpenAiWriterRoom_AcceptsKey_AndSendsBearerHeader()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}"));
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "api", url: null, provider: "openai", apiKey: "sk-test", CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(StudioProviders.OpenAi, provider);
        Assert.Equal("OpenAI - key accepted", detail);
        Assert.Equal("Bearer sk-test", handler.LastRequest!.Headers.Authorization!.ToString());
        Assert.Equal("https://api.openai.com/v1/models", handler.LastRequest.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task Test_OpenAiWriterRoom_RejectedKey_ReportsStatusCode()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.Unauthorized, new StringContent("{}"));
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "api", url: null, provider: "openai", apiKey: "sk-bad", CancellationToken.None);

        Assert.False(ok);
        Assert.Null(provider);
        Assert.Equal("OpenAI rejected the key (401).", detail);
    }

    [TestMethod]
    public async Task Test_OpenAi_OnNonWriterRoom_IsRefused()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "api", url: null, provider: "openai", apiKey: "sk-test", CancellationToken.None);

        Assert.False(ok);
        Assert.Null(provider);
        Assert.Equal("OpenAI is only available for writer rooms.", detail);
    }

    [TestMethod]
    public async Task Test_ApiProvider_WithoutKey_AsksForKey()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));

        var (openAiOk, _, openAiDetail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "api", url: null, provider: "openai", apiKey: " ", CancellationToken.None);
        var (elevenOk, _, elevenDetail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "api", url: null, provider: "elevenlabs", apiKey: null, CancellationToken.None);

        Assert.False(openAiOk);
        Assert.Equal("An API key is required.", openAiDetail);
        Assert.False(elevenOk);
        Assert.Equal("An API key is required.", elevenDetail);
    }

    [TestMethod]
    public async Task Test_ElevenLabsBooth_AcceptsKey_AndSendsXiHeader()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}"));
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "api", url: null, provider: "elevenlabs", apiKey: "xi-test", CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(StudioProviders.ElevenLabs, provider);
        Assert.Equal("ElevenLabs — key accepted", detail);
        Assert.Equal("xi-test", handler.LastRequest!.Headers.GetValues("xi-api-key").Single());
        Assert.Equal("https://api.elevenlabs.io/v1/user", handler.LastRequest.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task Test_ElevenLabs_OnWriterRoom_IsRefused()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));

        var (ok, _, detail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "api", url: null, provider: "elevenlabs", apiKey: "xi-test", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Writer room API endpoints use OpenAI.", detail);
    }

    [TestMethod]
    public async Task Test_UnknownApiProvider_IsRefused()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));

        var (ok, _, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "api", url: null, provider: "acme", apiKey: "key", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Unknown API provider 'acme'.", detail);
    }

    // ---- local endpoints ------------------------------------------------------

    [TestMethod]
    public async Task Test_LocalSource_RequiresAValidUrl()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));

        var (missingOk, _, missingDetail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "local", url: null, provider: null, apiKey: null, CancellationToken.None);
        var (invalidOk, _, invalidDetail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "local", url: "not a url", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(missingOk);
        Assert.Equal("A valid URL is required.", missingDetail);
        Assert.False(invalidOk);
        Assert.Equal("A valid URL is required.", invalidDetail);
    }

    [TestMethod]
    public async Task Test_WriterRoom_IdentifiesOllama_AndCountsModels()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { models = new[] { new { name = "gemma4:e4b" }, new { name = "nomic-embed" } } }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "local", "http://localhost:8001/", provider: null, apiKey: null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(StudioProviders.Ollama, provider);
        Assert.Equal("Ollama - 2 models", detail);
        Assert.Equal("http://localhost:8001/api/tags", handler.LastRequest!.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task Test_WriterRoom_NonSuccess_ReportsStatusCode()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.InternalServerError, new StringContent(""));
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.WriterRoom, "local", "http://localhost:8001", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(provider);
        Assert.Equal("GET /api/tags returned 500.", detail);
    }

    [TestMethod]
    public async Task Test_VoiceBooth_HealthyTtsSidecar_UsesLabelDetail()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "ok", label = "Qwen booth" }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "local", "http://localhost:8201", provider: null, apiKey: null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(StudioProviders.LocalTts, provider);
        Assert.Equal("Qwen booth", detail);
    }

    [TestMethod]
    public async Task Test_VoiceBooth_WithoutDetailFields_FallsBackToSidecarName()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "ok" }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, _, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "local", "http://localhost:8201", provider: null, apiKey: null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("TTS sidecar", detail);
    }

    [TestMethod]
    public async Task Test_VoiceBooth_BadStatus_IsReported()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "degraded" }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, _, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "local", "http://localhost:8201", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("TTS sidecar reports status 'degraded'.", detail);
    }

    [TestMethod]
    public async Task Test_Recording_IdentifiesAceStepEnvelope_WithVersionFallbackDetail()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new { status = "ok", version = "1.5.2" }, code = 200 }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8101", provider: null, apiKey: null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MusicBackends.AceStep, provider);
        Assert.Equal("ACE-Step 1.5.2", detail);
    }

    [TestMethod]
    public async Task Test_Recording_AceStepBadStatus_IsReported()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { data = new { status = "error" }, code = 500 }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, _, detail) = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8101", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("ACE-Step reports status 'error'.", detail);
    }

    [TestMethod]
    public async Task Test_Recording_IdentifiesMusicGenBackendFlag()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "ok", backends = new { musicgen = true } }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8102", provider: null, apiKey: null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(MusicBackends.MusicGen, provider);
        Assert.Equal("MusicGen sidecar", detail);
    }

    [TestMethod]
    public async Task Test_Recording_UnknownProtocol_IsRefused()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { hello = "world" }),
        });
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8102", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(provider);
        Assert.Equal("Endpoint answered but speaks no known studio protocol.", detail);
    }

    [TestMethod]
    public async Task Test_UnreachableEndpoint_ReturnsExceptionMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var coordinator = CreateCoordinator(handler);

        var (ok, provider, detail) = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "local", "http://localhost:8201", provider: null, apiKey: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(provider);
        Assert.Equal("connection refused", detail);
    }

    // ---- runtime state (no booking involved) ------------------------------------

    [TestMethod]
    public async Task GetRuntimeState_InactiveStudio_IsOff()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));
        var studio = new Studio { Id = Guid.NewGuid(), Name = "Booth", Kind = StudioKind.VoiceBooth, IsActive = false };

        var state = await coordinator.GetRuntimeStateAsync(studio, job: null, CancellationToken.None);

        Assert.Equal(StudioRuntimeState.Off, state.Status);
    }

    [TestMethod]
    public async Task GetRuntimeState_WithJob_IsBusyWithJobLabel()
    {
        var coordinator = CreateCoordinator(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("{}")));
        var studio = new Studio { Id = Guid.NewGuid(), Name = "Booth", Kind = StudioKind.VoiceBooth, IsActive = true };
        var job = new StudioJob("voicing intro", DateTime.UtcNow);

        var state = await coordinator.GetRuntimeStateAsync(studio, job, CancellationToken.None);

        Assert.Equal(StudioRuntimeState.Busy, state.Status);
        Assert.Equal("voicing intro", state.Detail);
    }

    [TestMethod]
    public async Task GetRuntimeState_ApiStudioWithoutUrl_IsReadyWithoutProbing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("should not be called"));
        var coordinator = CreateCoordinator(handler);
        var studio = new Studio
        {
            Id = Guid.NewGuid(),
            Name = "Eleven booth",
            Kind = StudioKind.VoiceBooth,
            Provider = StudioProviders.ElevenLabs,
            IsActive = true,
        };

        var state = await coordinator.GetRuntimeStateAsync(studio, job: null, CancellationToken.None);

        Assert.Equal(StudioRuntimeState.Ready, state.Status);
        Assert.Equal("API provider configured", state.Detail);
        Assert.Empty(handler.Requests);
    }

    [TestMethod]
    public async Task GetRuntimeState_HealthyLocalEndpoint_IsReady_AndOfflineWhenUnreachable()
    {
        var healthy = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "ok", label = "TTS sidecar" }),
        });
        var studio = new Studio
        {
            Id = Guid.NewGuid(),
            Name = "Booth",
            Kind = StudioKind.VoiceBooth,
            Url = "http://localhost:8201",
            Provider = StudioProviders.LocalTts,
            IsActive = true,
        };

        var readyState = await CreateCoordinator(healthy)
            .GetRuntimeStateAsync(studio, job: null, CancellationToken.None);
        Assert.Equal(StudioRuntimeState.Ready, readyState.Status);

        var unreachable = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var offlineState = await CreateCoordinator(unreachable)
            .GetRuntimeStateAsync(studio, job: null, CancellationToken.None);
        Assert.Equal(StudioRuntimeState.Offline, offlineState.Status);
    }

    // ---- harness ---------------------------------------------------------------

    private static StudioCoordinator CreateCoordinator(FakeHttpMessageHandler handler)
    {
        var clientFactory = new SingleClientFactory(() => handler.CreateClient());
        var publisher = new NoOpStudioUpdatePublisher();
        return new StudioCoordinator(
            new ThrowingDbContextFactory(),
            new StudioBookingRegistry(),
            new StudioEndpointProber(clientFactory),
            new StudioPendingOperationsTracker(publisher),
            publisher,
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            new OllamaModelMemoryManager(
                clientFactory,
                Options.Create(new LlmOptions { Model = "" }),
                new ThrowingDbContextFactory(),
                NullLogger<OllamaModelMemoryManager>.Instance),
            NullLogger<StudioCoordinator>.Instance);
    }

    private sealed class SingleClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }

    /// <summary>Connection tests never touch the database; fail loudly if they do.</summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext()
            => throw new InvalidOperationException("The connection test must not touch the database.");
    }
}
