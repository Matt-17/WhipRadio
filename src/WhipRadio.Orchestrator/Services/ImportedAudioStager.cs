using System.Diagnostics;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Helpers;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The analysis sidecar only sees the data volume (mounted read-only at /data)
/// and only decodes WAV reliably. Imported audio that lives outside the data
/// root (external folders) or is not WAV (MP3 uploads) is staged: transcoded
/// with local ffmpeg into <c>data/cache/analysis/</c>, analyzed from there,
/// and the temp file deleted afterwards. In-root WAVs pass through untouched.
/// </summary>
public sealed class ImportedAudioStager(
    IOptions<RadioOptions> radioOptions,
    IOptions<StreamOptions> streamOptions,
    ILogger<ImportedAudioStager> logger)
{
    private const string CacheSubdirectory = "cache/analysis";

    public sealed record StagedAudio(string SidecarRelativePath, string? TempAbsolutePath) : IDisposable
    {
        public void Dispose()
        {
            if (TempAbsolutePath is not null && File.Exists(TempAbsolutePath))
            {
                try
                {
                    File.Delete(TempAbsolutePath);
                }
                catch (IOException)
                {
                    // Still locked — the next stage of the same item overwrites it (-y).
                }
            }
        }
    }

    public async Task<StagedAudio> StageAsync(Guid itemId, string filePath, CancellationToken ct)
    {
        var dataRoot = radioOptions.Value.DataRoot;
        var absolute = MediaPaths.ResolveAbsolute(dataRoot, filePath);
        var isWav = absolute.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        if (isWav && MediaPaths.IsUnderDataRoot(dataRoot, filePath))
        {
            return new StagedAudio(filePath, TempAbsolutePath: null);
        }

        var relative = $"{CacheSubdirectory}/{itemId}.wav";
        var target = Path.Combine(dataRoot, CacheSubdirectory, $"{itemId}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await TranscodeToWavAsync(absolute, target, ct);
        logger.LogDebug("Staged {Source} for analysis at {Target}", absolute, target);
        return new StagedAudio(relative, target);
    }

    private async Task TranscodeToWavAsync(string source, string target, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = streamOptions.Value.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg failed to start for analysis staging.");
        // Drain both pipes concurrently so a large stdout can never deadlock the child,
        // and observe both reads rather than discarding the stdout drain.
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stderrTask, stdoutTask);
        if (process.ExitCode != 0)
        {
            string stderr = await stderrTask;
            throw new InvalidOperationException(
                $"ffmpeg staging failed (exit {process.ExitCode}): {Tail(stderr)}");
        }
    }

    private static string Tail(string text)
        => text.Length <= 400 ? text.Trim() : text[^400..].Trim();
}
