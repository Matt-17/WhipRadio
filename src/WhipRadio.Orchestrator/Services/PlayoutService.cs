using System.Diagnostics;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
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
    IPlaybackReporter reporter,
    IOptions<StreamOptions> streamOptions,
    IOptions<IcecastOptions> icecastOptions,
    IOptions<RadioOptions> radioOptions,
    ILogger<PlayoutService> logger) : BackgroundService
{
    private const int SilenceChunkMs = 500;
    private static readonly byte[] SilenceChunk = new byte[44100 * 2 * 2 * SilenceChunkMs / 1000];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
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
                logger.LogError(ex, "Encoder session ended unexpectedly; restarting in 5 s");
            }

            reporter.ReportIdle();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task RunEncoderSessionAsync(CancellationToken ct)
    {
        using var encoder = StartEncoder();
        logger.LogInformation(
            "Encoder started, pushing to icecast://{Host}:{Port}{Mount}",
            icecastOptions.Value.Host, icecastOptions.Value.Port, streamOptions.Value.Mount);

        var encoderInput = encoder.StandardInput.BaseStream;

        while (!ct.IsCancellationRequested)
        {
            if (encoder.HasExited)
            {
                throw new InvalidOperationException($"Encoder ffmpeg exited with code {encoder.ExitCode}.");
            }

            var item = await TryDequeueAsync(TimeSpan.FromSeconds(1), ct);
            if (item is null)
            {
                await encoderInput.WriteAsync(SilenceChunk, ct);
                await encoderInput.FlushAsync(ct);
                continue;
            }

            await reporter.ReportStartedAsync(item, ct);
            await PlayItemAsync(item, encoderInput, ct);
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

    private async Task PlayItemAsync(PlayoutItem item, Stream encoderInput, CancellationToken ct)
    {
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, item.FilePath);
        if (!File.Exists(absolutePath))
        {
            logger.LogWarning("Skipping missing file {Path}", absolutePath);
            return;
        }

        using var decoder = StartDecoder(absolutePath);
        try
        {
            await decoder.StandardOutput.BaseStream.CopyToAsync(encoderInput, ct);
            await encoderInput.FlushAsync(ct);
            await decoder.WaitForExitAsync(ct);
            if (decoder.ExitCode != 0)
            {
                logger.LogWarning("Decoder exited with code {Code} for {Path}", decoder.ExitCode, absolutePath);
            }
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

    private Process StartDecoder(string absolutePath)
        => StartFfmpeg(
            $"-hide_banner -loglevel error -i \"{absolutePath}\" -f s16le -ar 44100 -ac 2 pipe:1",
            redirectStdin: false,
            redirectStdout: true);

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
        return process;
    }
}
