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
using WhipRadio.TestSupport;

namespace WhipRadio.Infrastructure.Tests;

/// <summary>
/// The booking desk itself: two studios sharing the local GPU must never be
/// booked at the same time, and releases must free the GPU lease and record stats.
/// </summary>
[TestClass]
public class StudioCoordinatorBookingTests
{
    [TestMethod]
    public void GpuGroupForEndpoint_CollapsesLocalHostsAndKeepsRemoteOnes()
    {
        Assert.Equal("gpu:local", StudioCoordinator.GpuGroupForEndpoint("http://localhost:8001"));
        Assert.Equal("gpu:local", StudioCoordinator.GpuGroupForEndpoint("http://127.0.0.1:8101"));
        Assert.Equal("gpu:local", StudioCoordinator.GpuGroupForEndpoint("http://host.docker.internal:8201"));
        Assert.Equal("gpu:render-box", StudioCoordinator.GpuGroupForEndpoint("http://Render-Box:8101"));
        Assert.Null(StudioCoordinator.GpuGroupForEndpoint(null));
        Assert.Null(StudioCoordinator.GpuGroupForEndpoint("not a url"));
    }

    [TestMethod]
    public async Task TryAcquire_BlocksSecondStudioOnSameGpu_UntilReleased_AndRecordsStats()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid boothId;
        Guid studioId;
        await using (var db = fixture.CreateDbContext())
        {
            var booth = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #1",
                Kind = StudioKind.VoiceBooth,
                Url = "http://localhost:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            };
            var studio = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Studio #1",
                Kind = StudioKind.Recording,
                Url = "http://127.0.0.1:8101",
                Provider = MusicBackends.AceStep,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            };
            db.Studios.AddRange(booth, studio);
            await db.SaveChangesAsync();
            boothId = booth.Id;
            studioId = studio.Id;
        }

        var coordinator = CreateCoordinator(fixture);

        // Booth and studio use different hosts (localhost vs 127.0.0.1) but both
        // collapse to the shared gpu:local group — booking one blocks the other.
        var booth1 = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);
        Assert.NotNull(booth1);
        Assert.Equal(boothId, booth1!.Id);

        var blocked = await coordinator.TryAcquireAsync(
            StudioKind.Recording, requiredProvider: null, "recording song", CancellationToken.None);
        Assert.Null(blocked);

        await coordinator.ReleaseAsync(boothId, success: true, CancellationToken.None);

        var studioAfterRelease = await coordinator.TryAcquireAsync(
            StudioKind.Recording, requiredProvider: null, "recording song", CancellationToken.None);
        Assert.NotNull(studioAfterRelease);
        Assert.Equal(studioId, studioAfterRelease!.Id);

        await using (var db = fixture.CreateDbContext())
        {
            var booth = await db.Studios.AsNoTracking().FirstAsync(s => s.Id == boothId);
            Assert.Equal(1, booth.JobsCompleted);
            Assert.Equal(0, booth.JobsFailed);
            Assert.NotNull(booth.LastUsedAt);
        }
    }

    [TestMethod]
    public async Task TryAcquire_SkipsBookedStudio_AndBooksTheNextFreeOneOfTheSameKind()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid firstId;
        Guid secondId;
        await using (var db = fixture.CreateDbContext())
        {
            // Two remote booths on DIFFERENT hosts → different GPU groups, so only
            // the per-studio booking (not the GPU lease) arbitrates.
            var first = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #1",
                Kind = StudioKind.VoiceBooth,
                Url = "http://tts-host-a:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            };
            var second = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #2",
                Kind = StudioKind.VoiceBooth,
                Url = "http://tts-host-b:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            };
            db.Studios.AddRange(first, second);
            await db.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
        }

        var coordinator = CreateCoordinator(fixture);

        var booked1 = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "job A", CancellationToken.None);
        var booked2 = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "job B", CancellationToken.None);

        Assert.Equal(firstId, booked1?.Id);
        Assert.Equal(secondId, booked2?.Id);
        Assert.Equal(2, coordinator.ActiveJobs.Count);
    }

    [TestMethod]
    public async Task AcquireForGpuJob_ApiStudio_BooksWithoutGpuLease_AndCompleteIsIdempotent()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid boothId;
        await using (var db = fixture.CreateDbContext())
        {
            var booth = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Eleven booth",
                Kind = StudioKind.VoiceBooth,
                Provider = StudioProviders.ElevenLabs,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            };
            db.Studios.Add(booth);
            await db.SaveChangesAsync();
            boothId = booth.Id;
        }

        var coordinator = CreateCoordinator(fixture);

        var lease = await coordinator.AcquireForGpuJobAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(boothId, lease!.Studio.Id);
        Assert.Equal(1, coordinator.ActiveJobs.Count);
        Assert.Null(coordinator.ActiveJobs[boothId].GpuResourceGroup);
        Assert.Empty(coordinator.PendingOperations);

        await lease.CompleteAsync(success: true, CancellationToken.None);
        await lease.CompleteAsync(success: true, CancellationToken.None);

        Assert.Empty(coordinator.ActiveJobs);
        await using (var db = fixture.CreateDbContext())
        {
            var booth = await db.Studios.AsNoTracking().FirstAsync(s => s.Id == boothId);
            Assert.Equal(1, booth.JobsCompleted);
            Assert.Equal(0, booth.JobsFailed);
        }
    }

    [TestMethod]
    public async Task AcquireForGpuJob_LocalStudio_HoldsGpuLease_BlocksSameGpu_AndReleasesOnComplete()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid studioId;
        Guid boothId;
        await using (var db = fixture.CreateDbContext())
        {
            var studio = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Studio #1",
                Kind = StudioKind.Recording,
                Url = "http://localhost:8101",
                Provider = MusicBackends.AceStep,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            };
            var booth = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #1",
                Kind = StudioKind.VoiceBooth,
                Url = "http://localhost:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            };
            db.Studios.AddRange(studio, booth);
            await db.SaveChangesAsync();
            studioId = studio.Id;
            boothId = booth.Id;
        }

        var coordinator = CreateCoordinator(fixture);

        var lease = await coordinator.AcquireForGpuJobAsync(
            StudioKind.Recording, requiredProvider: null, "recording song", CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(studioId, lease!.Studio.Id);
        Assert.Equal("gpu:local", coordinator.ActiveJobs[studioId].GpuResourceGroup);
        Assert.Empty(coordinator.PendingOperations);

        // The booth shares gpu:local, so booking is refused while the studio job holds the lease.
        var blocked = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);
        Assert.Null(blocked);

        await lease.CompleteAsync(success: false, CancellationToken.None);
        Assert.Empty(coordinator.ActiveJobs);

        var booth1 = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);
        Assert.Equal(boothId, booth1?.Id);

        await using (var db = fixture.CreateDbContext())
        {
            var studio = await db.Studios.AsNoTracking().FirstAsync(s => s.Id == studioId);
            Assert.Equal(0, studio.JobsCompleted);
            Assert.Equal(1, studio.JobsFailed);
        }
    }

    [TestMethod]
    public async Task AcquireGpuTurn_TracksPendingOperation_UntilDisposed()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var coordinator = CreateCoordinator(fixture);

        var turn = await coordinator.AcquireGpuTurnAsync(
            StudioKind.WriterRoom, "http://localhost:11434", CancellationToken.None);

        var pending = coordinator.PendingOperations;
        Assert.Equal(1, pending.Count);
        Assert.Equal("Writing text", pending[0].Label);
        Assert.Equal(StudioPendingOperationStatus.Work, pending[0].Status);
        Assert.Equal("Running on default endpoint", pending[0].Detail);
        Assert.Equal("gpu:local", pending[0].ResourceGroup);
        Assert.Equal(StudioKind.WriterRoom, pending[0].Kind);

        await turn.DisposeAsync();
        Assert.Empty(coordinator.PendingOperations);
    }

    [TestMethod]
    public async Task AcquireGpuTurn_WithoutEndpoint_IsANoop()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var coordinator = CreateCoordinator(fixture);

        var turn = await coordinator.AcquireGpuTurnAsync(
            StudioKind.WriterRoom, endpointUrl: null, CancellationToken.None);

        Assert.Empty(coordinator.PendingOperations);
        await turn.DisposeAsync();
    }

    [TestMethod]
    public async Task UpdateJobProgress_TrimsAndStoresProgress_OnlyForBookedStudios()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid boothId;
        await using (var db = fixture.CreateDbContext())
        {
            var booth = new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #1",
                Kind = StudioKind.VoiceBooth,
                Url = "http://localhost:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            };
            db.Studios.Add(booth);
            await db.SaveChangesAsync();
            boothId = booth.Id;
        }

        var coordinator = CreateCoordinator(fixture);
        var booked = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);
        Assert.NotNull(booked);

        await coordinator.UpdateJobProgressAsync(boothId, "  42%  ", CancellationToken.None);
        Assert.Equal("42%", coordinator.ActiveJobs[boothId].Progress);

        await coordinator.UpdateJobProgressAsync(boothId, "   ", CancellationToken.None);
        Assert.Equal("42%", coordinator.ActiveJobs[boothId].Progress);

        // Unknown studios are ignored without throwing.
        await coordinator.UpdateJobProgressAsync(Guid.NewGuid(), "99%", CancellationToken.None);
        Assert.Equal(1, coordinator.ActiveJobs.Count);
    }

    [TestMethod]
    public async Task GetRuntimeState_StudioOnLeasedGpu_ReportsWhoHoldsTheGpu()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Studios.Add(new Studio
            {
                Id = Guid.NewGuid(),
                Name = "Booth #1",
                Kind = StudioKind.VoiceBooth,
                Url = "http://localhost:8201",
                Provider = StudioProviders.LocalTts,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
        }

        var coordinator = CreateCoordinator(fixture);
        var booked = await coordinator.TryAcquireAsync(
            StudioKind.VoiceBooth, requiredProvider: null, "voicing intro", CancellationToken.None);
        Assert.NotNull(booked);

        // A different studio on the same GPU group (localhost vs 127.0.0.1) is busy by proxy.
        var recordingStudio = new Studio
        {
            Id = Guid.NewGuid(),
            Name = "Studio #1",
            Kind = StudioKind.Recording,
            Url = "http://127.0.0.1:8101",
            Provider = MusicBackends.AceStep,
            IsActive = true,
        };

        var state = await coordinator.GetRuntimeStateAsync(recordingStudio, job: null, CancellationToken.None);

        Assert.Equal(StudioRuntimeState.Busy, state.Status);
        Assert.Equal("GPU reserved by voicing intro", state.Detail);
    }

    private static StudioCoordinator CreateCoordinator(DbFixture fixture)
    {
        // Every studio endpoint reports healthy so runtime probes never block booking.
        var handler = new FakeHttpMessageHandler(request =>
        {
            HttpContent content = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => JsonContent.Create(new { models = new[] { new { name = "gemma4:e4b" } } }),
                "/health" when request.RequestUri.Port == 8201
                    => JsonContent.Create(new { status = "ok", label = "TTS sidecar" }),
                "/health" => JsonContent.Create(new { data = new { status = "ok", version = "1.0" }, code = 200 }),
                _ => new StringContent("{}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        return new StudioCoordinator(
            fixture,
            new SingleClientFactory(() => handler.CreateClient()),
            new NoOpStudioUpdatePublisher(),
            new LocalGpuScheduler(NullLogger<LocalGpuScheduler>.Instance),
            new OllamaModelMemoryManager(
                new SingleClientFactory(() => handler.CreateClient()),
                Options.Create(new LlmOptions { Model = "" }),
                fixture,
                NullLogger<OllamaModelMemoryManager>.Instance),
            NullLogger<StudioCoordinator>.Instance);
    }

    private sealed class SingleClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }
}
