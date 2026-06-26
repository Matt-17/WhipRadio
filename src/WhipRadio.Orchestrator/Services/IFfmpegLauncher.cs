using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// A launched ffmpeg process as the playout loop sees it: the std streams it
/// pumps plus the exit/kill surface it polls. Extracted from a direct
/// <see cref="Process"/> dependency (mirroring <see cref="IMixerEncoderSink"/> /
/// <see cref="IPcmSampleReaderFactory"/>) so <see cref="PlayoutService"/>'s
/// restart loop, silence bridge and off-air abort can be driven in tests with
/// in-memory streams and no real ffmpeg.
/// </summary>
public interface IFfmpegProcess : IMixerEncoderSink, IDisposable
{
    /// <summary>Encoder stdin (s16le PCM in).</summary>
    Stream StandardInput { get; }

    /// <summary>Decoder stdout (s16le PCM out).</summary>
    Stream StandardOutput { get; }

    Task WaitForExitAsync(CancellationToken ct);

    void Kill();
}

/// <summary>
/// Spawns the two ffmpeg roles the playout loop needs: one long-lived encoder
/// pushing MP3 to Icecast (stdin = raw PCM) and a short-lived decoder per item
/// rendering a file to raw PCM (stdout).
/// </summary>
public interface IFfmpegLauncher
{
    IFfmpegProcess StartEncoder();

    IFfmpegProcess StartDecoder(string absolutePath, double startOffsetSeconds);
}

/// <summary>Default implementation backed by real <see cref="Process"/> instances.</summary>
public sealed class ProcessFfmpegLauncher(
    IOptions<StreamOptions> streamOptions,
    IOptions<IcecastOptions> icecastOptions,
    FfmpegProcessRegistry ffmpegRegistry,
    ILogger<ProcessFfmpegLauncher> logger) : IFfmpegLauncher
{
    public IFfmpegProcess StartEncoder()
    {
        var stream = streamOptions.Value;
        var icecast = icecastOptions.Value;
        var target = $"icecast://{icecast.SourceUser}:{icecast.SourcePassword}@{icecast.Host}:{icecast.Port}{stream.Mount}";

        // -re paces the pipe read at realtime so silence and audio stay in sync with the stream clock.
        var args =
            $"-hide_banner -loglevel warning -re -f s16le -ar 44100 -ac 2 -i pipe:0 " +
            $"-c:a libmp3lame -b:a {stream.Bitrate} -content_type audio/mpeg -f mp3 \"{target}\"";

        return Start(args, redirectStdin: true, redirectStdout: false);
    }

    public IFfmpegProcess StartDecoder(string absolutePath, double startOffsetSeconds)
    {
        var seek = startOffsetSeconds > 0
            ? $"-ss {startOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)} "
            : string.Empty;

        return Start(
            $"-hide_banner -loglevel error {seek}-i \"{absolutePath}\" -f s16le -ar 44100 -ac 2 pipe:1",
            redirectStdin: false,
            redirectStdout: true);
    }

    private ProcessFfmpegHandle Start(string arguments, bool redirectStdin, bool redirectStdout)
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
        return new ProcessFfmpegHandle(process);
    }
}

/// <summary>
/// Wraps a real ffmpeg <see cref="Process"/>. Via <see cref="IFfmpegProcess"/>
/// it is also an <see cref="IMixerEncoderSink"/>, so the encoder handle can be
/// handed straight to the mixer without a separate adapter.
/// </summary>
public sealed class ProcessFfmpegHandle(Process process) : IFfmpegProcess
{
    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

    public void Kill() => process.Kill(entireProcessTree: true);

    public void Dispose() => process.Dispose();
}
