using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Owns ONE long-lived ffmpeg encoder pushing MP3 to Icecast and feeds it raw
/// PCM: for each queue item a short-lived decoder ffmpeg renders the WAV to
/// s16le/44.1k/stereo which is copied into the encoder's stdin. Queue gaps are
/// bridged with silence so the mount never drops. Encoder crash ⇒ restart, then
/// continue with the next item (Plan.md §2, M6.4).
/// </summary>
public class PlayoutService(
    IPlayoutQueue queue,
    PlayoutStateStore stateStore,
    IPlaybackReporter reporter,
    TrackDeletionService trackDeletions,
    AudioMixerEngine mixerEngine,
    FfmpegProcessRegistry ffmpegRegistry,
    EncoderHeartbeat heartbeat,
    IStationMetrics metrics,
    IStationStatusReporter statusReporter,
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<StreamOptions> streamOptions,
    IOptions<IcecastOptions> icecastOptions,
    IOptions<RadioOptions> radioOptions,
    ILogger<PlayoutService> logger) : BackgroundService
{
    private const int SilenceChunkMs = 500;
    private static readonly byte[] SilenceChunk = new byte[44100 * 2 * 2 * SilenceChunkMs / 1000];
    private static readonly TimeSpan ParkReEnablePollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = streamOptions.Value;
        var policy = new EncoderResiliencePolicy(
            window: TimeSpan.FromMinutes(stream.EncoderCrashWindowMinutes),
            threshold: stream.EncoderCrashThreshold,
            initialBackoff: TimeSpan.FromSeconds(stream.EncoderInitialBackoffSeconds),
            maxBackoff: TimeSpan.FromSeconds(stream.EncoderMaxBackoffSeconds),
            successResetsAfter: TimeSpan.FromSeconds(stream.EncoderSuccessResetsAfterSeconds),
            nowUtc: DateTime.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            policy.MarkSessionStart(DateTime.UtcNow);
            try
            {
                await RunEncoderSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.EncoderRestarted();

                var nowUtc = DateTime.UtcNow;
                if (policy.RecordCrash(nowUtc))
                {
                    // Circuit breaker: stop hot-looping ffmpeg into a dead Icecast.
                    // Park the station (PlayoutEnabled := false) and surface Offline
                    // until an operator re-enables On Air.
                    logger.LogCritical(
                        "Encoder circuit breaker tripped: {Crashes} crashes in {Window} min. " +
                        "Parking station — re-enable On Air to resume.",
                        policy.CrashesInWindow, stream.EncoderCrashWindowMinutes);
                    await ParkStationAsync(stoppingToken);
                    policy.Reset();
                    continue;
                }

                var backoff = policy.NextBackoff();
                statusReporter.Set(
                    StationStatus.Reconnecting,
                    ShortReason(ex),
                    nowUtc + backoff);
                logger.LogError(ex,
                    "Encoder session ended unexpectedly; restarting in {Backoff}s ({Crashes} recent crashes)",
                    backoff.TotalSeconds, policy.CrashesInWindow);

                reporter.ReportIdle();
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Park the station after the circuit breaker trips: persist
    /// <c>PlayoutEnabled = false</c> so the console switch reflects the parked
    /// state, surface "Offline" to the UI, then block until an operator
    /// re-enables On Air. Never throws — a failed persist still leaves us
    /// waiting on the manual toggle.
    /// </summary>
    private async Task ParkStationAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.GetStationSettingsOrDefaultAsync(ct);
            if (settings.PlayoutEnabled)
            {
                settings.PlayoutEnabled = false;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Station parked: PlayoutEnabled set to false by circuit breaker");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist PlayoutEnabled=false while parking; waiting for manual re-enable anyway");
        }

        statusReporter.Set(
            StationStatus.Offline,
            "Encoder circuit breaker tripped — re-enable On Air to resume.");
        reporter.ReportIdle();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ParkReEnablePollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (await IsPlayoutEnabledAsync(ct))
            {
                logger.LogInformation("Station re-enabled after circuit-breaker park; resuming encoder");
                return;
            }
        }
    }

    private static string ShortReason(Exception ex)
    {
        var msg = ex.Message?.Trim();
        if (string.IsNullOrEmpty(msg))
        {
            return ex.GetType().Name;
        }

        return msg.Length <= 160 ? msg : $"{msg[..157]}...";
    }

    private async Task RunEncoderSessionAsync(CancellationToken ct)
    {
        using var encoder = StartEncoder();
        statusReporter.Set(StationStatus.Online);
        logger.LogInformation(
            "Encoder started, pushing to icecast://{Host}:{Port}{Mount}",
            icecastOptions.Value.Host, icecastOptions.Value.Port, streamOptions.Value.Mount);

        var encoderInput = encoder.StandardInput.BaseStream;
        var offAir = false;

        while (!ct.IsCancellationRequested)
        {
            heartbeat.LastBeatUtc = DateTime.UtcNow;

            if (encoder.HasExited)
            {
                throw new InvalidOperationException($"Encoder ffmpeg exited with code {encoder.ExitCode}.");
            }

            // Off air = the mount keeps streaming silence so listeners stay
            // connected; now-playing clears immediately (ON AIR lamp goes dark).
            if (!await IsPlayoutEnabledAsync(ct))
            {
                if (!offAir)
                {
                    offAir = true;
                    reporter.ReportIdle();
                    logger.LogInformation("Off air — streaming silence until re-enabled");
                }

                await encoderInput.WriteAsync(SilenceChunk, ct);
                await encoderInput.FlushAsync(ct);
                continue;
            }

            if (offAir)
            {
                offAir = false;
                logger.LogInformation("Back on air");
            }

            // Phase 3a: the real-time mixer takes over the feed while enabled;
            // it returns at an item boundary when the flag flips off and the
            // legacy sequential loop below resumes (shared encoder).
            if (await IsMixerEnabledAsync(ct))
            {
                logger.LogInformation("Mixer engaged");
                await mixerEngine.RunSessionAsync(new ProcessEncoderSink(encoder), encoderInput,
                    async token => await IsPlayoutEnabledAsync(token) && await IsMixerEnabledAsync(token), ct);
                logger.LogInformation("Mixer disengaged — legacy playout resumes");
                continue;
            }

            if (queue.PeekNext() is { ItemType: PlayoutItemType.Track }
                && await ShouldHoldTrackForTopOfHourPackageAsync(ct))
            {
                await encoderInput.WriteAsync(SilenceChunk, ct);
                await encoderInput.FlushAsync(ct);
                continue;
            }

            var item = await TryDequeueAsync(TimeSpan.FromSeconds(1), ct);
            if (item is null)
            {
                await encoderInput.WriteAsync(SilenceChunk, ct);
                await encoderInput.FlushAsync(ct);
                continue;
            }

            stateStore.MarkStarted(item);
            trackDeletions.MarkPlaybackStarted(item);
            await reporter.ReportStartedAsync(item, ct);
            try
            {
                await PlayItemAsync(item, encoderInput, ct); // aborted items land in the off-air branch above
            }
            finally
            {
                stateStore.Complete(item);
                await trackDeletions.MarkPlaybackCompletedAsync(item, ct);
            }
        }
    }

    private async Task<bool> ShouldHoldTrackForTopOfHourPackageAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!settings.NewsEnabled && !settings.WeatherEnabled)
            {
                return false;
            }

            var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
            var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
            var minTarget = now.AddSeconds(-lateWindow);
            var maxTarget = now.AddSeconds(introGrace);
            return await db.NewsPackages.AsNoTracking()
                .AnyAsync(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc >= minTarget
                    && package.TargetUtc <= maxTarget
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying
                        || package.Status == NewsPackageStatus.Ready
                        || package.Status == NewsPackageStatus.Queued), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not evaluate top-of-hour legacy playout guard");
            return false;
        }
    }

    private async Task<bool> IsTopOfHourDueAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!settings.NewsEnabled && !settings.WeatherEnabled)
            {
                return false;
            }

            var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
            var minTarget = now.AddSeconds(-lateWindow);
            return await db.NewsPackages.AsNoTracking()
                .AnyAsync(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc <= now
                    && package.TargetUtc >= minTarget
                    && (package.Status == NewsPackageStatus.Ready
                        || package.Status == NewsPackageStatus.Queued), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not evaluate top-of-hour due check");
            return false;
        }
    }

    private async Task<bool> IsMixerEnabledAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).MixerEnabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false; // db hiccup → safe legacy path
        }
    }

    private async Task<bool> IsPlayoutEnabledAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            return settings.PlayoutEnabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return true; // never let a db hiccup take the station down
        }
    }

    private async Task<PlayoutItem?> TryDequeueAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await queue.DequeueAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <returns>false when playback was aborted because the station went off air.</returns>
    private async Task<bool> PlayItemAsync(PlayoutItem item, Stream encoderInput, CancellationToken ct)
    {
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, item.FilePath);
        if (!File.Exists(absolutePath))
        {
            logger.LogWarning("Skipping missing file {Path}", absolutePath);
            return true;
        }

        using var decoder = StartDecoder(absolutePath, item.StartOffsetSeconds);
        try
        {
            // Manual pump instead of CopyToAsync so the off-air switch is honored
            // mid-item: the admin expects the station to fall silent within seconds,
            // not after the current track finishes. -re backpressure paces the loop.
            var buffer = new byte[32 * 1024];
            var decoderOutput = decoder.StandardOutput.BaseStream;
            var lastEnabledCheck = DateTime.UtcNow;
            int bytesRead;

            while ((bytesRead = await decoderOutput.ReadAsync(buffer, ct)) > 0)
            {
                await encoderInput.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                if (DateTime.UtcNow - lastEnabledCheck >= TimeSpan.FromSeconds(2))
                {
                    lastEnabledCheck = DateTime.UtcNow;
                    if (!await IsPlayoutEnabledAsync(ct))
                    {
                        logger.LogInformation("Off-air switch flipped — aborting \"{Title}\" mid-item", item.Title);
                        return false;
                    }

                    if (item.ItemType == PlayoutItemType.Track
                        && await IsTopOfHourDueAsync(ct))
                    {
                        logger.LogInformation(
                            "Top-of-hour package due — aborting \"{Title}\" mid-item to let package play",
                            item.Title);
                        return true;
                    }
                }
            }

            await encoderInput.FlushAsync(ct);
            await decoder.WaitForExitAsync(ct);
            if (decoder.ExitCode != 0)
            {
                logger.LogWarning("Decoder exited with code {Code} for {Path}", decoder.ExitCode, absolutePath);
            }

            return true;
        }
        finally
        {
            if (!decoder.HasExited)
            {
                decoder.Kill(entireProcessTree: true);
            }
        }
    }

    private Process StartEncoder()
    {
        var stream = streamOptions.Value;
        var icecast = icecastOptions.Value;
        var target = $"icecast://{icecast.SourceUser}:{icecast.SourcePassword}@{icecast.Host}:{icecast.Port}{stream.Mount}";

        // -re paces the pipe read at realtime so silence and audio stay in sync with the stream clock.
        var args =
            $"-hide_banner -loglevel warning -re -f s16le -ar 44100 -ac 2 -i pipe:0 " +
            $"-c:a libmp3lame -b:a {stream.Bitrate} -content_type audio/mpeg -f mp3 \"{target}\"";

        return StartFfmpeg(args, redirectStdin: true, redirectStdout: false);
    }

    private Process StartDecoder(string absolutePath, double startOffsetSeconds)
    {
        var seek = startOffsetSeconds > 0
            ? $"-ss {startOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)} "
            : string.Empty;

        return StartFfmpeg(
            $"-hide_banner -loglevel error {seek}-i \"{absolutePath}\" -f s16le -ar 44100 -ac 2 pipe:1",
            redirectStdin: false,
            redirectStdout: true);
    }

    private Process StartFfmpeg(string arguments, bool redirectStdin, bool redirectStdout)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = streamOptions.Value.FfmpegPath,
                Arguments = arguments,
                RedirectStandardInput = redirectStdin,
                RedirectStandardOutput = redirectStdout,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                logger.LogDebug("ffmpeg: {Line}", e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        ffmpegRegistry.Register(process); // next startup kills it if we crash
        return process;
    }
}
