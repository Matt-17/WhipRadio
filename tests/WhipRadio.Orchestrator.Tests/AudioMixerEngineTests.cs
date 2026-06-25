using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

/// <summary>
/// Drives the real-time <see cref="AudioMixerEngine"/> state machine without ffmpeg
/// or audio files: a fake reader factory produces in-memory tone PCM, a pacing
/// stream absorbs the encoder feed, and a throwing DB factory exercises the
/// designed no-analysis degradation path. These cover the highest-risk
/// previously-untested code in the station (crossfade scheduling, early-EOF
/// handling, off-air handback, queue starvation).
/// </summary>
[TestClass]
public class AudioMixerEngineTests
{
    private static readonly PcmFormat Format = new();

    private static PlayoutItem Track(string title, double seconds)
        => new(PlayoutItemType.Track, Guid.NewGuid(), $"library/tracks/{title}.wav", title, seconds);

    private sealed record Fixture(
        AudioMixerEngine Mixer,
        FakeQueue Queue,
        FakeReporter Reporter,
        TrackDeletionService TrackDeletions,
        FakeReaderFactory Readers,
        TimedPlayoutInterruptService TimedInterrupts,
        CollectingLogger Logger)
    {
        public static Fixture Create(
            Func<PlayoutItem, double>? audioDuration = null,
            bool collectLogs = false,
            IDbContextFactory<RadioDbContext>? dbFactory = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "whipradio-mixer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            dbFactory ??= new ThrowingDbContextFactory();
            var radioOptions = Options.Create(new RadioOptions { DataRoot = root });
            var queue = new FakeQueue();
            var reporter = new FakeReporter();
            var stateStore = new PlayoutStateStore(radioOptions, TimeProvider.System,
                NullLogger<PlayoutStateStore>.Instance);
            var trackDeletions = new TrackDeletionService(dbFactory, radioOptions,
                NullLogger<TrackDeletionService>.Instance);
            var planner = new MixPlanner(new SystemRandomSource(seed: 42));
            var diagnostics = new MixerDiagnostics();
            var mixerUpdates = new NoOpMixerUpdatePublisher();
            var timedInterrupts = new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance);
            var readers = new FakeReaderFactory(audioDuration);
            var logger = new CollectingLogger();
            var fallback = new EmergencyFallbackTrackService(
                dbFactory,
                new QueueStateTracker(),
                radioOptions,
                NullLogger<EmergencyFallbackTrackService>.Instance);
            var mixer = new AudioMixerEngine(
                queue, reporter, stateStore, trackDeletions, fallback, planner, diagnostics, mixerUpdates,
                timedInterrupts, readers, NullStationMetrics.Instance, dbFactory,
                collectLogs ? logger : NullLogger<AudioMixerEngine>.Instance);
            return new Fixture(mixer, queue, reporter, trackDeletions, readers, timedInterrupts, logger);
        }
    }

    [TestMethod]
    public async Task EmptyQueue_StreamsSilenceOnly_AndNeverReports()
    {
        var fix = Fixture.Create();
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 300, delayMs: 0);

        // Always wanted → only exits via the pacing cancel.
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Empty(fix.Reporter.Starts);
        Assert.True(pace.NonZeroBytes == 0, "idle playout must emit silence, not tone");
        Assert.InRange(pace.Writes, 290, 320);
    }

    [TestMethod]
    public async Task SingleItem_ReportsStarted_AndCompletes()
    {
        var fix = Fixture.Create();
        var item = Track("solo", seconds: 2);
        fix.Queue.Enqueue(item);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // safety
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, delayMs: 1);

        // Wanted until the item becomes visible, then hand back at the next boundary.
        var seen = false;
        fix.Reporter.OnStarted = _ => seen = true;
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(!seen), cts.Token);

        Assert.False(cts.IsCancellationRequested, "session should return on its own, not via the safety cancel");
        Assert.Equal(1, fix.Reporter.Starts.Count);
        Assert.Equal(item.ItemId, fix.Reporter.Starts[0].ItemId);
        Assert.False(fix.TrackDeletions.IsTrackActive(item.ItemId),
            "item must be released from the active-playback set once it finishes");
    }

    [TestMethod]
    public async Task OffAirFlip_LetsCurrentItemFinish_BeforeHandingBack()
    {
        var fix = Fixture.Create();
        // 4 s item: the off-air flag flips at the ~2 s flag-check, well before EOF,
        // so the mixer must keep playing the current item to its natural end.
        var item = Track("long", seconds: 4);
        fix.Queue.Enqueue(item);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // safety
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, delayMs: 1);

        var seen = false;
        fix.Reporter.OnStarted = _ => seen = true;
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(!seen), cts.Token);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(1, fix.Reporter.Starts.Count);
        Assert.False(fix.TrackDeletions.IsTrackActive(item.ItemId));
        // 4 s of stereo 44.1k = ~172 frames; the item must play substantially to
        // completion, not be aborted at the 2 s flag-check.
        Assert.InRange(pace.Writes, 165, 185);
    }

    [TestMethod]
    public async Task TwoSongs_HardCutTransition_BothReported()
    {
        var fix = Fixture.Create();
        var first = Track("a", seconds: 2);
        var second = Track("b", seconds: 2);
        fix.Queue.Enqueue(first);
        fix.Queue.Enqueue(second);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // safety
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, delayMs: 1);

        // Wanted until both items have been reported, then hand back.
        var reported = 0;
        fix.Reporter.OnStarted = _ => Interlocked.Increment(ref reported);
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(reported < 2), cts.Token);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(2, fix.Reporter.Starts.Count);
        Assert.Equal(first.ItemId, fix.Reporter.Starts[0].ItemId);
        Assert.Equal(second.ItemId, fix.Reporter.Starts[1].ItemId);
        Assert.False(fix.TrackDeletions.IsTrackActive(first.ItemId));
        Assert.False(fix.TrackDeletions.IsTrackActive(second.ItemId));
    }

    [TestMethod]
    public async Task EncoderExitedAtStart_ThrowsAndDoesNotDeadlock()
    {
        var fix = Fixture.Create();
        var item = Track("x", seconds: 1);
        fix.Queue.Enqueue(item);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sink = new FakeEncoderSink(hasExited: true);
        var pace = new PacingStream(cts, delayMs: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token));
    }

    [TestMethod]
    public async Task EarlyEof_LogsShorterThanMetadata_AndContinuesWithoutCrash()
    {
        // Metadata claims 5 s but the audio stream ends after 0.2 s — the mixer
        // must log the diagnostic and complete the item rather than hang.
        var fix = Fixture.Create(audioDuration: _ => 0.2, collectLogs: true);
        var item = Track("broken", seconds: 5);
        fix.Queue.Enqueue(item);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, delayMs: 1);

        var seen = false;
        fix.Reporter.OnStarted = _ => seen = true;
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(!seen), cts.Token);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(1, fix.Reporter.Starts.Count);
        Assert.False(fix.TrackDeletions.IsTrackActive(item.ItemId));
        Assert.Contains(fix.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("shorter than duration"));
    }

    [TestMethod]
    public async Task TimedPackage_CanStartInsideIntroGraceBeforeTarget()
    {
        var fix = Fixture.Create();
        var package = new PlayoutItem(
            PlayoutItemType.Announcement,
            Guid.NewGuid(),
            "library/announcements/news.wav",
            "Top of hour - news and weather",
            1);
        var track = Track("should-not-start-first", seconds: 2);
        fix.Queue.Enqueue(track);
        fix.TimedInterrupts.Schedule(new TimedPlayoutInterrupt(
            package,
            DateTime.UtcNow.AddSeconds(1),
            FadeOutSeconds: 1,
            GraceSeconds: 15,
            LateWindowSeconds: 300));
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 180, delayMs: 1);

        var seenPackage = false;
        fix.Reporter.OnStarted = item => seenPackage = item.ItemId == package.ItemId;
        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(!seenPackage), cts.Token);

        Assert.True(fix.Reporter.Starts.Count > 0);
        Assert.Equal(package.ItemId, fix.Reporter.Starts[0].ItemId);
    }

    [TestMethod]
    public async Task PendingPackage_KeepsPlayingMusicInsideTopOfHourWindow()
    {
        // A package still in production (Pending) has no audio to air yet. The mixer
        // must keep playing music — never stop the song and stream silence — until the
        // package becomes Ready. Only Ready/Queued packages hold the track start.
        await using var db = await DbFixture.CreateAsync();
        await db.SetPackageAsync(NewsPackageStatus.Pending, DateTime.UtcNow.AddSeconds(-1));
        var fix = Fixture.Create(dbFactory: db);
        var track = Track("keeps-playing", seconds: 2);
        fix.Queue.Enqueue(track);
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 120, delayMs: 0);

        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Contains(fix.Reporter.Starts, item => item.ItemId == track.ItemId);
        Assert.True(pace.NonZeroBytes > 0, "music should keep playing while the package is pending");
    }

    [TestMethod]
    public async Task QueuedPackage_HoldsTrackStartInsideTopOfHourWindow()
    {
        await using var db = await DbFixture.CreateAsync();
        await db.SetPackageAsync(NewsPackageStatus.Queued, DateTime.UtcNow.AddSeconds(-1));
        var fix = Fixture.Create(dbFactory: db);
        fix.Queue.Enqueue(Track("must-wait", seconds: 2));
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 120, delayMs: 0);

        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Empty(fix.Reporter.Starts);
        Assert.Equal(0, pace.NonZeroBytes);
    }

    [TestMethod]
    public async Task PendingPackage_AllowsTransitionIntoTrackAfterBoundary()
    {
        // While the package is still in production (Pending), normal programming
        // continues — the next track is allowed to start even past the boundary.
        // The hold only engages once the package is Ready/Queued.
        await using var db = await DbFixture.CreateAsync();
        await db.SetIntroGraceAsync(0);
        await db.SetPackageAsync(NewsPackageStatus.Pending, DateTime.UtcNow.AddMilliseconds(50));
        var fix = Fixture.Create(dbFactory: db);
        var first = Track("current", seconds: 2);
        var second = Track("next-allowed", seconds: 2);
        fix.Queue.Enqueue(first);
        fix.Queue.Enqueue(second);
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 220, delayMs: 1);

        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Contains(fix.Reporter.Starts, item => item.ItemId == first.ItemId);
        Assert.Contains(fix.Reporter.Starts, item => item.ItemId == second.ItemId);
    }

    [TestMethod]
    public async Task PendingPackage_DoesNotFadeCurrentTrackAtBoundary()
    {
        // The reported bug: a package still being produced (Pending) caused the mixer
        // to fade the playing song out at the top of the hour — airing silence while
        // it waited for news that wasn't ready. A Pending package must leave the music
        // alone; the fade only happens for a Ready/Queued package that can actually air.
        await using var db = await DbFixture.CreateAsync();
        var fix = Fixture.Create(dbFactory: db, collectLogs: true);
        var longTrack = Track("long-current", seconds: 120);
        fix.Queue.Enqueue(longTrack);
        using var cts = new CancellationTokenSource();
        var sink = new FakeEncoderSink(hasExited: false);
        var pace = new PacingStream(cts, cancelAfterWrites: 260, delayMs: 1);

        var inserted = false;
        fix.Reporter.OnStarted = item =>
        {
            if (!inserted && item.ItemId == longTrack.ItemId)
            {
                inserted = true;
                db.SetPackageAsync(NewsPackageStatus.Pending, DateTime.UtcNow.AddSeconds(-1))
                    .GetAwaiter()
                    .GetResult();
            }
        };

        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Contains(fix.Reporter.Starts, item => item.ItemId == longTrack.ItemId);
        // The bug was the mixer fading the song out at the boundary for a pending
        // package; its absence is the direct signal the music was left alone.
        Assert.DoesNotContain(fix.Logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("fading current audio"));
        Assert.True(pace.NonZeroBytes > 0, "music should keep playing, not silence");
    }

    [TestMethod]
    public async Task TimedPackage_IsNotFadedOutByTopOfHourHold_WhilePackageStillQueued()
    {
        // Regression: once the timed interrupt put the package composite on air, the
        // still-active top-of-hour hold faded EVERY active source — the package
        // included — so the package went silent ~2 s in and (after the dispatcher
        // cleared the guard) a song started. The hold must fade music only, never
        // the scheduled package announcement. With no track on air, the hold must
        // therefore do nothing — and must NOT log that it is fading current audio.
        await using var db = await DbFixture.CreateAsync();
        await db.SetIntroGraceAsync(0);
        // Queued package whose boundary just passed → the top-of-hour hold is active
        // for the whole session (the dispatcher, not the mixer, flips it to Played).
        await db.SetPackageAsync(NewsPackageStatus.Queued, DateTime.UtcNow.AddMilliseconds(-200));
        var fix = Fixture.Create(dbFactory: db, collectLogs: true);

        // The package must outlast the 60 s "let the current item finish" grace, or
        // the hold would let it ride regardless — real top-of-hour packages run ~3 min.
        var package = new PlayoutItem(
            PlayoutItemType.Announcement,
            Guid.NewGuid(),
            "library/announcements/news.wav",
            "Top of hour - news and weather",
            120);
        fix.TimedInterrupts.Schedule(new TimedPlayoutInterrupt(
            package,
            DateTime.UtcNow.AddMilliseconds(-100),
            FadeOutSeconds: 1,
            GraceSeconds: 15,
            LateWindowSeconds: 300));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sink = new FakeEncoderSink(hasExited: false);
        // ~4 s of audio — long enough for the buggy hold to fade the just-started
        // package, but a tiny fraction of the 120 s package so a healthy one is
        // still squarely on air.
        var pace = new PacingStream(cts, cancelAfterWrites: 180, delayMs: 1);

        await fix.Mixer.RunSessionAsync(sink, pace, _ => Task.FromResult(true), cts.Token);

        Assert.Contains(fix.Reporter.Starts, item => item.ItemId == package.ItemId);
        // No track is on air, so the only thing the hold could be "fading" is the
        // package composite itself — which is the bug.
        Assert.DoesNotContain(fix.Logger.Entries,
            e => e.Message.Contains("fading current audio"));
        // And the package must actually be producing audio, not silence.
        Assert.True(pace.NonZeroBytes > 0, "the top-of-hour package produced no audio");
    }

    // --- fakes -----------------------------------------------------------------

    private sealed class FakeQueue : IPlayoutQueue
    {
        private readonly Queue<PlayoutItem> _q = new();
        public int Count => _q.Count;

        public void Enqueue(PlayoutItem item) => _q.Enqueue(item);

        public void EnqueueFront(PlayoutItem item)
        {
            var rest = _q.ToList();
            _q.Clear();
            _q.Enqueue(item);
            foreach (var i in rest)
            {
                _q.Enqueue(i);
            }
        }

        public PlayoutItem? PeekNext() => _q.TryPeek(out var i) ? i : null;

        public async Task<PlayoutItem> DequeueAsync(CancellationToken ct)
        {
            if (_q.TryDequeue(out var item))
            {
                return item;
            }

            // Mirror the real blocking queue, but return fast so the mixer's
            // timeout branch resolves immediately instead of waiting 1 s.
            await Task.Delay(2, ct).ConfigureAwait(false);
            throw new OperationCanceledException();
        }
    }

    private sealed class FakeReporter : IPlaybackReporter
    {
        public List<PlayoutItem> Starts { get; } = [];
        public Action<PlayoutItem>? OnStarted;

        public Task ReportStartedAsync(PlayoutItem item, CancellationToken ct)
        {
            Starts.Add(item);
            OnStarted?.Invoke(item);
            return Task.CompletedTask;
        }

        public void ReportIdle() { }
    }

    private sealed class FakeEncoderSink : IMixerEncoderSink
    {
        public FakeEncoderSink(bool hasExited) => HasExited = hasExited;
        public bool HasExited { get; }
        public int ExitCode => -1;
    }

    private sealed class FakeReaderFactory(Func<PlayoutItem, double>? audioDuration) : IPcmSampleReaderFactory
    {
        public IPcmSampleReader Create(PlayoutItem item, PcmFormat format, double startAtSeconds)
        {
            var seconds = audioDuration?.Invoke(item) ?? item.DurationSeconds;
            return new TonePcmReader(format, seconds, amplitude: 1000);
        }
    }

    private sealed class TonePcmReader(PcmFormat format, double durationSeconds, short amplitude) : IPcmSampleReader
    {
        private long _remaining = (long)Math.Round(durationSeconds * format.SampleRate * format.Channels);
        private bool _eof;

        public bool EndOfStream => _eof;

        public int Read(Span<short> frame)
        {
            if (_remaining <= 0)
            {
                _eof = true;
                return 0;
            }

            var n = (int)Math.Min(frame.Length, _remaining);
            for (var i = 0; i < n; i++)
            {
                frame[i] = amplitude;
            }

            _remaining -= n;
            if (_remaining <= 0)
            {
                _eof = true;
            }

            return n;
        }
    }

    private sealed class PacingStream : Stream
    {
        private readonly CancellationTokenSource _cancel;
        private readonly int? _cancelAfterWrites;
        private readonly int _delayMs;
        private int _writes;
        private long _nonZeroBytes;

        public int Writes => _writes;
        public long NonZeroBytes => _nonZeroBytes;

        public PacingStream(CancellationTokenSource cancel, int? cancelAfterWrites = null, int delayMs = 0)
        {
            _cancel = cancel;
            _cancelAfterWrites = cancelAfterWrites;
            _delayMs = delayMs;
        }

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writes++;
            var span = buffer.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] != 0)
                {
                    _nonZeroBytes++;
                }
            }

            if (_cancelAfterWrites is { } limit && _writes >= limit)
            {
                _cancel.Cancel();
                return;
            }

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext() => throw new InvalidOperationException("no DB in mixer tests");
        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no DB in mixer tests");
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            DbContextOptions<RadioDbContext> options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (RadioDbContext db = new(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.StationSettings.Add(new StationSettings
                {
                    Id = StationSettings.SingletonId,
                    NewsEnabled = true,
                    TopOfHourIntroGraceSeconds = 15,
                    DefaultCrossfadeSeconds = 1,
                });
                await db.SaveChangesAsync();
            }

            return new DbFixture(connection, options);
        }

        public async Task SetPackageAsync(NewsPackageStatus status, DateTime targetUtc)
        {
            await using RadioDbContext db = CreateDbContext();
            Guid? announcementId = null;
            if (status is NewsPackageStatus.Ready or NewsPackageStatus.Queued)
            {
                if (!await db.Moderators.AnyAsync(moderator => moderator.Id == 1))
                {
                    db.Moderators.Add(new Moderator
                    {
                        Id = 1,
                        Name = "Maya",
                        Language = "en",
                        Gender = ModeratorGenders.Female,
                        TtsEngine = TtsEngines.Kokoro,
                        VoiceId = "af_bella",
                    });
                }

                announcementId = Guid.NewGuid();
                db.Announcements.Add(new Announcement
                {
                    Id = announcementId.Value,
                    ModeratorId = 1,
                    Kind = AnnouncementKind.Weather,
                    ScriptText = "weather",
                    VoicedText = "weather",
                    FilePath = "library/announcements/weather.wav",
                    DurationSeconds = 30,
                    CreatedAt = DateTime.UtcNow,
                    PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly,
                });
            }

            db.NewsPackages.Add(new NewsPackage
            {
                Id = Guid.NewGuid(),
                Kind = NewsPackageKind.TopOfHour,
                Status = status,
                TargetUtc = targetUtc,
                TargetDurationSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow,
                AnnouncementId = announcementId,
            });
            await db.SaveChangesAsync();
        }

        public async Task SetIntroGraceAsync(int seconds)
        {
            await using RadioDbContext db = CreateDbContext();
            var settings = await db.StationSettings.FirstAsync();
            settings.TopOfHourIntroGraceSeconds = seconds;
            await db.SaveChangesAsync();
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class NoOpMixerUpdatePublisher : IMixerUpdatePublisher
    {
        public void Publish() { }
        public Task PublishAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CollectingLogger : ILogger<AudioMixerEngine>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
