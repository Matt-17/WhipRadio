using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

/// <summary>
/// Drives the legacy <see cref="PlayoutService"/> encoder loop without a real
/// ffmpeg via the <see cref="IFfmpegLauncher"/> seam: a fake encoder exposes a
/// controllable exit flag and an in-memory stdin stream. These cover the
/// previously-untested reliability paths — crash-restart backoff, the empty-queue
/// silence bridge, and honoring the off-air switch — that decide whether the
/// mount stays alive at 3am.
/// </summary>
[TestClass]
public class PlayoutServiceTests
{
    private static PlayoutItem Track(string title, double seconds)
        => new(PlayoutItemType.Track, Guid.NewGuid(), $"library/tracks/{title}.wav", title, seconds);

    [TestMethod]
    public async Task EncoderExitsImmediately_ReportsReconnecting_AndBacksOff()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // safety net
        var reporter = new FakeStatusReporter(onReconnecting: cts.Cancel);
        var launcher = new FakeFfmpegLauncher(new FakeFfmpegProcess(Stream.Null, hasExited: true, exitCode: 1));

        var fix = Fixture.Create(launcher, reporter); // no DB needed on this path

        await RunExecuteAsync(fix.Service, cts.Token);

        // The crashed session must surface as Online → Reconnecting (not an infinite
        // silent hot-loop), and never report a now-playing item.
        Assert.Contains(StationStatus.Online, reporter.Statuses);
        Assert.Contains(StationStatus.Reconnecting, reporter.Statuses);
        Assert.Equal(StationStatus.Online, reporter.Statuses[0]);
        Assert.Empty(fix.Reporter.Starts);
    }

    [TestMethod]
    public async Task EmptyQueue_WhileEnabled_StreamsSilenceOnly()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // safety net
        var stdin = new CapturingStream(cts, cancelAfterWrites: 5);
        var launcher = new FakeFfmpegLauncher(new FakeFfmpegProcess(stdin, hasExited: false, exitCode: 0));

        // ThrowingDbContextFactory → IsPlayoutEnabledAsync defaults to enabled and the
        // fallback selector returns null, so the loop bridges the gap with silence.
        var fix = Fixture.Create(launcher, new FakeStatusReporter());

        await RunExecuteAsync(fix.Service, cts.Token);

        Assert.True(stdin.Writes > 0, "an enabled-but-empty station must keep feeding the mount");
        Assert.Equal(0, stdin.NonZeroBytes); // silence, not tone
        Assert.Empty(fix.Reporter.Starts);
    }

    [TestMethod]
    public async Task OffAir_DoesNotPlayQueuedTrack_StreamsSilence_AndGoesIdle()
    {
        await using var db = await DbFixture.CreateAsync(playoutEnabled: false);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // safety net
        var stdin = new CapturingStream(cts, cancelAfterWrites: 5);
        var launcher = new FakeFfmpegLauncher(new FakeFfmpegProcess(stdin, hasExited: false, exitCode: 0));

        var fix = Fixture.Create(launcher, new FakeStatusReporter(), dbFactory: db);
        fix.Queue.Enqueue(Track("must-not-play", seconds: 2));

        await RunExecuteAsync(fix.Service, cts.Token);

        // Off air: the queued track must never start, the mount keeps streaming
        // silence, and now-playing is cleared (ReportIdle).
        Assert.Empty(fix.Reporter.Starts);
        Assert.Equal(1, fix.Queue.Count); // track still queued, untouched
        Assert.True(stdin.Writes > 0);
        Assert.Equal(0, stdin.NonZeroBytes);
        Assert.True(fix.Reporter.IdleCount > 0, "off air must clear the now-playing lamp");
    }

    // --- harness ---------------------------------------------------------------

    private static Task RunExecuteAsync(PlayoutService service, CancellationToken ct)
    {
        var method = typeof(PlayoutService).GetMethod(
            "ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [ct])!;
    }

    private sealed record Fixture(PlayoutService Service, FakeQueue Queue, FakeReporter Reporter)
    {
        public static Fixture Create(
            IFfmpegLauncher launcher,
            IStationStatusReporter statusReporter,
            IDbContextFactory<RadioDbContext>? dbFactory = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "whipradio-playout-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            dbFactory ??= new ThrowingDbContextFactory();
            var radioOptions = Options.Create(new RadioOptions { DataRoot = root });
            var streamOptions = Options.Create(new StreamOptions());
            var icecastOptions = Options.Create(new IcecastOptions());

            var queue = new FakeQueue();
            var reporter = new FakeReporter();
            var stateStore = new PlayoutStateStore(radioOptions, TimeProvider.System,
                NullLogger<PlayoutStateStore>.Instance);
            var trackDeletions = new TrackDeletionService(dbFactory, radioOptions,
                NullLogger<TrackDeletionService>.Instance);
            var fallback = new EmergencyFallbackTrackService(dbFactory, new QueueStateTracker(),
                radioOptions, NullLogger<EmergencyFallbackTrackService>.Instance);
            var mixer = new AudioMixerEngine(
                queue, reporter, stateStore, trackDeletions, fallback,
                new MixPlanner(new SystemRandomSource(seed: 1)), new MixerDiagnostics(),
                new NoOpMixerUpdatePublisher(),
                new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance),
                new ThrowingReaderFactory(), NullStationMetrics.Instance, dbFactory,
                NullLogger<AudioMixerEngine>.Instance);

            var service = new PlayoutService(
                queue, stateStore, reporter, trackDeletions, fallback, mixer, launcher,
                new EncoderHeartbeat(TimeProvider.System), NullStationMetrics.Instance,
                statusReporter, dbFactory, streamOptions, icecastOptions, radioOptions,
                NullLogger<PlayoutService>.Instance);

            return new Fixture(service, queue, reporter);
        }
    }

    // --- fakes -----------------------------------------------------------------

    private sealed class FakeFfmpegLauncher(FakeFfmpegProcess encoder) : IFfmpegLauncher
    {
        public IFfmpegProcess StartEncoder() => encoder;

        public IFfmpegProcess StartDecoder(string absolutePath, double startOffsetSeconds)
            => throw new NotSupportedException("no track playback in these tests");
    }

    private sealed class FakeFfmpegProcess(Stream stdin, bool hasExited, int exitCode) : IFfmpegProcess
    {
        public Stream StandardInput => stdin;
        public Stream StandardOutput => throw new NotSupportedException();
        public bool HasExited => hasExited;
        public int ExitCode => exitCode;
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }

    /// <summary>Captures the encoder feed and cancels the session after N writes so
    /// the loop terminates deterministically.</summary>
    private sealed class CapturingStream(CancellationTokenSource cancel, int cancelAfterWrites) : Stream
    {
        public int Writes { get; private set; }
        public long NonZeroBytes { get; private set; }

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

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            foreach (var b in buffer.Span)
            {
                if (b != 0)
                {
                    NonZeroBytes++;
                }
            }

            if (Writes >= cancelAfterWrites)
            {
                cancel.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStatusReporter(Action? onReconnecting = null) : IStationStatusReporter
    {
        public List<StationStatus> Statuses { get; } = [];
        public List<bool> PlayoutEnabledChanges { get; } = [];
        public StationStatusInfo Current { get; private set; } = StationStatusInfo.Online;

        public void Set(StationStatus status, string? reason = null, DateTime? nextAttemptUtc = null)
        {
            Statuses.Add(status);
            Current = Current with { Status = status, Reason = reason, NextAttemptUtc = nextAttemptUtc };
            if (status == StationStatus.Reconnecting)
            {
                onReconnecting?.Invoke();
            }
        }

        public void SetPlayoutEnabled(bool enabled)
        {
            PlayoutEnabledChanges.Add(enabled);
            Current = Current with { PlayoutEnabled = enabled };
        }

        public Task PublishAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

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

            await Task.Delay(2, ct).ConfigureAwait(false);
            throw new OperationCanceledException();
        }
    }

    private sealed class FakeReporter : IPlaybackReporter
    {
        public List<PlayoutItem> Starts { get; } = [];
        public int IdleCount { get; private set; }

        public Task ReportStartedAsync(PlayoutItem item, CancellationToken ct)
        {
            Starts.Add(item);
            return Task.CompletedTask;
        }

        public void ReportIdle() => IdleCount++;
    }

    private sealed class ThrowingReaderFactory : IPcmSampleReaderFactory
    {
        public IPcmSampleReader Create(PlayoutItem item, PcmFormat format, double startAtSeconds)
            => throw new NotSupportedException("mixer is never engaged in these tests");
    }

    private sealed class NoOpMixerUpdatePublisher : IMixerUpdatePublisher
    {
        public void Publish() { }
        public Task PublishAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext() => throw new InvalidOperationException("no DB on this path");
        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no DB on this path");
    }
}
