using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class WriterRoomStudioTests
{
    [TestMethod]
    public async Task StudioCoordinator_TestAsync_RecognizesLocalOllamaWriterRoom()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { models = new[] { new { name = "gemma4:e4b" } } }));
        var coordinator = new StudioCoordinator(
            new ThrowingDbFactory(),
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var result = await coordinator.TestAsync(
            StudioKind.WriterRoom, "local", "http://localhost:11434", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(StudioProviders.Ollama, result.Provider);
        Assert.Contains("Ollama", result.Detail);
        Assert.Equal("/api/tags", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_TestAsync_RecognizesOpenAiWriterRoom()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { data = Array.Empty<object>() }));
        var coordinator = new StudioCoordinator(
            new ThrowingDbFactory(),
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var result = await coordinator.TestAsync(
            StudioKind.WriterRoom, "api", null, StudioProviders.OpenAi, "sk-test", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(StudioProviders.OpenAi, result.Provider);
        Assert.Contains("OpenAI", result.Detail);
        Assert.Equal("Bearer sk-test", handler.LastRequest!.Headers.Authorization?.ToString());
    }

    [TestMethod]
    public async Task StudioCoordinator_TestAsync_RecognizesLocalVoiceBoothUsingHealth()
    {
        const string Label = "Qwen3-TTS 12Hz - 0.6B synth / 1.7B voice design";
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { status = "ok", engine = "qwen", label = Label }));
        var coordinator = new StudioCoordinator(
            new ThrowingDbFactory(),
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var result = await coordinator.TestAsync(
            StudioKind.VoiceBooth, "local", "http://localhost:8201", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(StudioProviders.LocalTts, result.Provider);
        Assert.Equal(Label, result.Detail);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_TestAsync_RecognizesLocalMusicGenUsingHealthLabel()
    {
        const string Label = "MusicGen - musicgen-small";
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new
            {
                status = "ok",
                service = "whipradio-musicgen",
                label = Label,
                backends = new Dictionary<string, bool> { [MusicBackends.MusicGen] = true },
            }));
        var coordinator = new StudioCoordinator(
            new ThrowingDbFactory(),
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var result = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8002", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(MusicBackends.MusicGen, result.Provider);
        Assert.Equal(Label, result.Detail);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_TestAsync_RecognizesAceStepUsingHealthLabel()
    {
        const string Label = "ACE-Step 1.5 Turbo";
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { data = new { status = "ok", version = "1.5", label = Label }, code = 200 }));
        var coordinator = new StudioCoordinator(
            new ThrowingDbFactory(),
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var result = await coordinator.TestAsync(
            StudioKind.Recording, "local", "http://localhost:8101", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(MusicBackends.AceStep, result.Provider);
        Assert.Equal(Label, result.Detail);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_RuntimeState_ReportsOfflineLocalRecordingStudio()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var studioId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.Add(new Studio
            {
                Id = studioId,
                Name = "Studio #1",
                Kind = StudioKind.Recording,
                Url = "http://studio.local",
                Provider = MusicBackends.AceStep,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.ServiceUnavailable,
            new StringContent("starting"));
        var coordinator = new StudioCoordinator(
            fixture,
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        await using var verify = fixture.CreateDbContext();
        var studio = await verify.Studios.SingleAsync();
        var state = await coordinator.GetRuntimeStateAsync(studio, job: null, CancellationToken.None);

        Assert.Equal(StudioRuntimeState.Offline, state.Status);
        Assert.Contains("GET /health returned 503", state.Detail);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_TryAcquireAsync_SkipsOfflineLocalRecordingStudio()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.Add(new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Studio #1",
                Kind = StudioKind.Recording,
                Url = "http://studio.local",
                Provider = MusicBackends.AceStep,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.ServiceUnavailable,
            new StringContent("starting"));
        var coordinator = new StudioCoordinator(
            fixture,
            new SingleClientFactory(handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var studio = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording for test artist", CancellationToken.None);

        Assert.Null(studio);
        Assert.Empty(coordinator.ActiveJobs);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task StudioCoordinator_TryAcquireAsync_QueuesLocalRecordingStudiosOnSameGpuHost()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var firstStudioId = Guid.NewGuid();
        var secondStudioId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.AddRange(
                new Studio
                {
                    Id = firstStudioId,
                    Name = "Studio #1",
                    Kind = StudioKind.Recording,
                    Url = "http://localhost:8101",
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                },
                new Studio
                {
                    Id = secondStudioId,
                    Name = "Studio #2",
                    Kind = StudioKind.Recording,
                    Url = "http://127.0.0.1:8102",
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.AddSeconds(1),
                });
            await db.SaveChangesAsync();
        }

        var coordinator = new StudioCoordinator(
            fixture,
            ReadyLocalStudiosClientFactory(),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var first = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording first", CancellationToken.None);
        var second = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording second", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(firstStudioId, first!.Id);
        Assert.Null(second);
        Assert.True(await coordinator.AnyBusyAsync(StudioKind.Recording, MusicBackends.AceStep, CancellationToken.None));
        Assert.False(await coordinator.AnyAvailableAsync(StudioKind.Recording, MusicBackends.AceStep, CancellationToken.None));

        await coordinator.ReleaseAsync(first.Id, success: true, CancellationToken.None);
        second = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording second", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(firstStudioId, second!.Id);
    }

    [TestMethod]
    public async Task StudioCoordinator_TryAcquireAsync_QueuesWriterRoomBehindLocalRecordingGpuLease()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var studioId = Guid.NewGuid();
        var writerRoomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.AddRange(
                new Studio
                {
                    Id = studioId,
                    Name = "Studio #1",
                    Kind = StudioKind.Recording,
                    Url = "http://localhost:8101",
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                },
                new Studio
                {
                    Id = writerRoomId,
                    Name = "Writer Room #1",
                    Kind = StudioKind.WriterRoom,
                    Url = "http://localhost:11434",
                    Provider = StudioProviders.Ollama,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.AddSeconds(1),
                });
            await db.SaveChangesAsync();
        }

        var coordinator = new StudioCoordinator(
            fixture,
            ReadyLocalStudiosClientFactory(),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var studio = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording music", CancellationToken.None);
        var writerRoom = await coordinator.TryAcquireAsync(
            StudioKind.WriterRoom, StudioProviders.Ollama, "Writing copy", CancellationToken.None);

        Assert.NotNull(studio);
        Assert.Null(writerRoom);
        Assert.True(await coordinator.AnyBusyAsync(StudioKind.WriterRoom, StudioProviders.Ollama, CancellationToken.None));
        Assert.False(await coordinator.AnyAvailableAsync(StudioKind.WriterRoom, StudioProviders.Ollama, CancellationToken.None));

        await using var verify = fixture.CreateDbContext();
        var writerStudio = await verify.Studios.SingleAsync(s => s.Id == writerRoomId);
        var state = await coordinator.GetRuntimeStateAsync(writerStudio, job: null, CancellationToken.None);
        Assert.Equal(StudioRuntimeState.Busy, state.Status);
        Assert.Contains("GPU reserved", state.Detail);
    }

    [TestMethod]
    public async Task StudioCoordinator_TryAcquireAsync_AllowsLocalGpuStudiosOnDifferentHosts()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.AddRange(
                new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Studio #1",
                    Kind = StudioKind.Recording,
                    Url = "http://studio-a.local:8101",
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                },
                new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Studio #2",
                    Kind = StudioKind.Recording,
                    Url = "http://studio-b.local:8101",
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.AddSeconds(1),
                });
            await db.SaveChangesAsync();
        }

        var coordinator = new StudioCoordinator(
            fixture,
            ReadyLocalStudiosClientFactory(),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);

        var first = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording first", CancellationToken.None);
        var second = await coordinator.TryAcquireAsync(
            StudioKind.Recording, MusicBackends.AceStep, "Recording second", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
    }

    [TestMethod]
    public async Task TextGenerationRouter_UsesActiveWriterRoomAndUpdatesUsageStats()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var writerRoomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
            db.Studios.Add(new Studio
            {
                Id = writerRoomId,
                Name = "Writer Room #2",
                Kind = StudioKind.WriterRoom,
                Url = "http://writer-room.local",
                Provider = StudioProviders.Ollama,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = new FakeHttpMessageHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath == "/api/tags"
                ? JsonContent.Create(new { models = new[] { new { name = "gemma4:e4b" } } })
                : JsonContent.Create(new { message = new { role = "assistant", content = "Fresh copy." } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var clientFactory = new SingleClientFactory(() => handler.CreateClient("http://fallback.local"));
        var coordinator = new StudioCoordinator(
            fixture,
            clientFactory,
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);
        var llmOptions = Options.Create(new LlmOptions { Model = "test-model" });
        var router = new TextGenerationRouter(
            clientFactory,
            llmOptions,
            new StationSettingsCache(fixture, TimeProvider.System),
            coordinator,
            History(fixture),
            NullLogger<TextGenerationRouter>.Instance,
            NullLoggerFactory.Instance);

        var result = await router.CompleteAsync("system", "user", "Writing weather report", CancellationToken.None);

        Assert.Equal("Fresh copy.", result);
        Assert.Equal("writer-room.local", handler.LastRequest!.RequestUri!.Host);

        await using var verify = fixture.CreateDbContext();
        var writerRoom = await verify.Studios.SingleAsync(s => s.Id == writerRoomId);
        Assert.Equal(1, writerRoom.JobsCompleted);
        Assert.Equal(0, writerRoom.JobsFailed);
        Assert.NotNull(writerRoom.LastUsedAt);
        var history = await verify.StudioHistory.SingleAsync();
        Assert.Equal(writerRoomId, history.StudioId);
        Assert.Equal(StudioHistoryStatus.Succeeded, history.Status);
        Assert.Equal("Writing weather report", history.Operation);
        Assert.Contains("System prompt:", history.Prompt);
        Assert.Contains("user", history.Prompt);
        Assert.Equal("Fresh copy.", history.Result);
    }

    [TestMethod]
    public async Task TextGenerationRouter_PreservesLegacyOpenAiSettingWhenNoOpenAiWriterRoomExists()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var writerRoomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                TextProvider = TextProviders.OpenAi,
                OpenAiApiKey = "sk-legacy",
                OpenAiModel = "gpt-test",
            });
            db.Studios.Add(new Studio
            {
                Id = writerRoomId,
                Name = "Writer Room #1",
                Kind = StudioKind.WriterRoom,
                Url = "http://writer-room.local",
                Provider = StudioProviders.Ollama,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new
            {
                choices = new[] { new { message = new { role = "assistant", content = "Cloud copy." } } },
            }));
        var clientFactory = new SingleClientFactory(handler.CreateClient("https://api.openai.test"));
        var coordinator = new StudioCoordinator(
            fixture,
            clientFactory,
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            StubModelMemory(),
            NullLogger<StudioCoordinator>.Instance);
        var llmOptions = Options.Create(new LlmOptions { Model = "test-model" });
        var router = new TextGenerationRouter(
            clientFactory,
            llmOptions,
            new StationSettingsCache(fixture, TimeProvider.System),
            coordinator,
            History(fixture),
            NullLogger<TextGenerationRouter>.Instance,
            NullLoggerFactory.Instance);

        var result = await router.CompleteAsync("system", "user", "Planning station day", CancellationToken.None);

        Assert.Equal("Cloud copy.", result);
        Assert.Equal("/v1/chat/completions", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer sk-legacy", handler.LastRequest.Headers.Authorization?.ToString());

        await using var verify = fixture.CreateDbContext();
        var writerRoom = await verify.Studios.SingleAsync(s => s.Id == writerRoomId);
        Assert.Equal(0, writerRoom.JobsCompleted);
        Assert.Null(writerRoom.LastUsedAt);
        var history = await verify.StudioHistory.SingleAsync();
        Assert.Null(history.StudioId);
        Assert.Equal("Writer Room (OpenAI settings)", history.StudioName);
        Assert.Equal(StudioProviders.OpenAi, history.Provider);
        Assert.Equal("Planning station day", history.Operation);
        Assert.Equal("Cloud copy.", history.Result);
    }

    private static StudioHistoryRecorder History(DbFixture fixture)
        => new(
            fixture,
            new NoOpStudioUpdatePublisher(),
            NullLogger<StudioHistoryRecorder>.Instance);

    // These tests drive TryAcquireAsync/runtime probing directly, so the model-memory
    // manager is never actually invoked — a stub with no-op deps is enough.
    private static OllamaModelMemoryManager StubModelMemory()
        => new(
            new SingleClientFactory(new HttpClient()),
            Options.Create(new LlmOptions()),
            new ThrowingDbFactory(),
            NullLogger<OllamaModelMemoryManager>.Instance);

    private static SingleClientFactory ReadyLocalStudiosClientFactory()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            HttpContent content = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => JsonContent.Create(new { models = new[] { new { name = "gemma4:e4b" } } }),
                "/health" => JsonContent.Create(new { data = new { status = "ok", version = "1.0" }, code = 200 }),
                _ => new StringContent("{}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        return new SingleClientFactory(() => handler.CreateClient("http://fallback.local"));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpClient> createClient;

        public SingleClientFactory(HttpClient client)
            : this(() => client)
        {
        }

        public SingleClientFactory(Func<HttpClient> createClient)
        {
            this.createClient = createClient;
        }

        public HttpClient CreateClient(string name) => createClient();
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext() => throw new InvalidOperationException("Database was not expected.");

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Database was not expected.");
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var db = new RadioDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DbFixture(connection, options);
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
