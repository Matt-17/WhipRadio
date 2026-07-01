using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class TopOfHourPackageDispatcherTests
{
    private static readonly DateTime TargetUtc = new(2026, 6, 21, 22, 30, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task RunCycle_DispatchesReadyWeatherPackageWhenNewsIsDisabled()
    {
        await using var db = await DbFixture.CreateAsync();
        var announcementId = Guid.NewGuid();
        await db.SeedPackageAsync(announcementId, NewsPackageStatus.Ready);
        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var dispatcher = new TopOfHourPackageDispatcher(
            db,
            new FakePlayoutQueue(),
            interrupts,
            new FixedTimeProvider(TargetUtc.AddSeconds(10)),
            new NoOpProductionUpdatePublisher(),
            NullLogger<TopOfHourPackageDispatcher>.Instance);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        var interrupt = interrupts.TryConsume(TargetUtc.AddSeconds(10));
        Assert.NotNull(interrupt);
        Assert.Equal(announcementId, interrupt!.Item.ItemId);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        Assert.Null(interrupts.TryConsume(TargetUtc.AddSeconds(12)));
    }

    [TestMethod]
    public async Task RunCycle_DoesNotDuplicateQueuedPackageDispatchInTheSameWindow()
    {
        await using var db = await DbFixture.CreateAsync();
        var announcementId = Guid.NewGuid();
        await db.SeedPackageAsync(announcementId, NewsPackageStatus.Queued);
        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var dispatcher = new TopOfHourPackageDispatcher(
            db,
            new FakePlayoutQueue(),
            interrupts,
            new FixedTimeProvider(TargetUtc.AddSeconds(10)),
            new NoOpProductionUpdatePublisher(),
            NullLogger<TopOfHourPackageDispatcher>.Instance);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        var interrupt = interrupts.TryConsume(TargetUtc.AddSeconds(10));
        Assert.NotNull(interrupt);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        Assert.Null(interrupts.TryConsume(TargetUtc.AddSeconds(12)));
    }

    [TestMethod]
    public async Task RunCycle_RecoversQueuedPackageIfNoPendingInterruptExists()
    {
        await using var db = await DbFixture.CreateAsync();
        var announcementId = Guid.NewGuid();
        await db.SeedPackageAsync(announcementId, NewsPackageStatus.Queued);
        var interrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
        var dispatcher = new TopOfHourPackageDispatcher(
            db,
            new FakePlayoutQueue(),
            interrupts,
            new FixedTimeProvider(TargetUtc.AddSeconds(10)),
            new NoOpProductionUpdatePublisher(),
            NullLogger<TopOfHourPackageDispatcher>.Instance);

        var interrupt = interrupts.TryConsume(TargetUtc.AddSeconds(10));
        Assert.Null(interrupt);

        await dispatcher.RunCycleForTestsAsync(CancellationToken.None);

        interrupt = interrupts.TryConsume(TargetUtc.AddSeconds(10));
        Assert.NotNull(interrupt);
        Assert.Equal(announcementId, interrupt!.Item.ItemId);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakePlayoutQueue : IPlayoutQueue
    {
        public int Count => 0;
        public void Enqueue(PlayoutItem item) { }
        public void EnqueueFront(PlayoutItem item) { }
        public PlayoutItem? PeekNext() => null;
        public Task<PlayoutItem> DequeueAsync(CancellationToken ct)
            => Task.FromException<PlayoutItem>(new InvalidOperationException("Queue should not be consumed."));
    }

    private sealed class NoOpProductionUpdatePublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // Local fixture (Postgres-backed) with the package seed helper this suite needs.
    private sealed class DbFixture : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly DbContextOptions<RadioDbContext> _options;

        private DbFixture(string connectionString)
        {
            _connectionString = connectionString;
            _options = new DbContextOptionsBuilder<RadioDbContext>().UseNpgsql(connectionString).Options;
        }

        public static async Task<DbFixture> CreateAsync()
            => new(await PostgresTestDatabase.CreateDatabaseAsync());

        public async Task SeedPackageAsync(Guid announcementId, NewsPackageStatus status)
        {
            await using RadioDbContext db = CreateDbContext();
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                NewsEnabled = false,
                WeatherEnabled = true,
                TopOfHourIntroGraceSeconds = 15,
                TopOfHourFadeOutSeconds = 1,
                MixerEnabled = true,
            });
            db.Moderators.Add(new Moderator
            {
                Id = 1,
                Name = "Maya",
                Language = "en",
                Gender = ModeratorGenders.Female,
                TtsEngine = TtsEngines.Kokoro,
                VoiceId = "af_bella",
            });
            db.Announcements.Add(new Announcement
            {
                Id = announcementId,
                ModeratorId = 1,
                Kind = AnnouncementKind.Weather,
                ScriptText = "weather",
                VoicedText = "weather",
                FilePath = "library/announcements/weather.wav",
                DurationSeconds = 30,
                CreatedAt = TargetUtc.AddMinutes(-5),
                PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly,
            });
            db.NewsPackages.Add(new NewsPackage
            {
                Id = Guid.NewGuid(),
                Kind = NewsPackageKind.TopOfHour,
                Status = status,
                TargetUtc = TargetUtc,
                TargetDurationSeconds = 300,
                CreatedAtUtc = TargetUtc.AddMinutes(-5),
                AnnouncementId = announcementId,
                QueuedAtUtc = status == NewsPackageStatus.Queued ? TargetUtc.AddSeconds(-10) : null,
            });
            await db.SaveChangesAsync();
        }

        public RadioDbContext CreateDbContext() => new(_options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await PostgresTestDatabase.DropDatabaseAsync(_connectionString);
    }
}
