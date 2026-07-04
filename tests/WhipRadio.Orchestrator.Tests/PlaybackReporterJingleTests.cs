using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class PlaybackReporterJingleTests
{
    [TestMethod]
    public async Task ReportStarted_Jingle_LogsPlayAndBumpsUsage()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var jingleId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Jingles.Add(new Jingle
            {
                Id = jingleId,
                Label = "Whip FM Sting",
                FilePath = "library/jingles/sting.wav",
                DurationSeconds = 11.5,
                Status = JingleStatus.Ready,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var stateRoot = Path.Combine(
            Path.GetTempPath(), "whipradio-reporter-jingle-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var nowPlaying = new NowPlayingState();
            var reporterLog = new RecordingLogger<PlaybackReporter>();
            var reporter = new PlaybackReporter(
                fixture,
                nowPlaying,
                new QueueStateTracker(),
                new PlayoutStateStore(
                    Options.Create(new RadioOptions { DataRoot = stateRoot }),
                    TimeProvider.System,
                    NullLogger<PlayoutStateStore>.Instance),
                new NullHubContext(),
                new StubHttpClientFactory(),
                new ScheduleService(fixture, TimeProvider.System),
                Options.Create(new IcecastOptions()),
                Options.Create(new StreamOptions { DisplayLatencySeconds = 0 }),
                reporterLog);

            var item = new PlayoutItem(
                PlayoutItemType.Jingle, jingleId, "library/jingles/sting.wav",
                "Station ID — Whip FM Sting", 11.5, ModeratorId: null);
            await reporter.ReportStartedAsync(item, CancellationToken.None);

            // The visible flip is fire-and-forget (zero display latency here) — poll briefly.
            PlayLogEntry? entry = null;
            for (var i = 0; i < 100 && entry is null; i++)
            {
                await Task.Delay(50);
                await using var db = fixture.CreateDbContext();
                entry = await db.PlayLog.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.ItemId == jingleId && e.ItemType == PlayoutItemType.Jingle);
            }

            Assert.True(entry is not null,
                $"Jingle play-log row never appeared. Reporter log: {reporterLog.Dump()}");
            Assert.Equal(11.5, entry!.DurationSeconds, precision: 3);
            Assert.Null(entry.ModeratorId);

            await using (var db = fixture.CreateDbContext())
            {
                var jingle = await db.Jingles.AsNoTracking().FirstAsync(j => j.Id == jingleId);
                Assert.Equal(1, jingle.PlayCount);
                Assert.NotNull(jingle.LastUsedAtUtc);
            }

            // The now-playing flip happens a beat after the play-log insert inside
            // the same delayed report — give it the same brief polling window.
            for (var i = 0; i < 100 && nowPlaying.Current is null; i++)
            {
                await Task.Delay(50);
            }

            Assert.True(nowPlaying.Current is not null,
                $"Now-playing never flipped. Reporter log: {reporterLog.Dump()}");
            Assert.Equal(PlayoutItemType.Jingle, nowPlaying.Current!.ItemType);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, recursive: true);
            }
        }
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<string> _entries = [];

        public string Dump()
        {
            lock (_entries)
            {
                return _entries.Count == 0 ? "(empty)" : string.Join(" | ", _entries);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" => {exception}")}");
            }
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class NullHubContext : IHubContext<RadioHub>
    {
        public IHubClients Clients { get; } = new NullHubClients();

        public IGroupManager Groups { get; } = new NullGroupManager();
    }

    private sealed class NullHubClients : IHubClients
    {
        public IClientProxy All { get; } = NullClientProxy.Instance;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public IClientProxy Client(string connectionId) => NullClientProxy.Instance;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullClientProxy.Instance;

        public IClientProxy Group(string groupName) => NullClientProxy.Instance;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;

        public IClientProxy User(string userId) => NullClientProxy.Instance;

        public IClientProxy Users(IReadOnlyList<string> userIds) => NullClientProxy.Instance;
    }

    private sealed class NullClientProxy : IClientProxy
    {
        public static readonly NullClientProxy Instance = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
