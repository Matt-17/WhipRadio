using System.Diagnostics;
using Microsoft.Extensions.Options;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Tracks every ffmpeg this orchestrator spawns in a pidfile so the NEXT
/// startup can kill orphans from a crashed/killed previous run — Windows does
/// not kill child processes with their parent, and a leftover encoder keeps
/// pushing stale audio to the Icecast mount ("weird sounds after restart").
/// Works regardless of how the app is started (VS, scripts, Aspire).
/// </summary>
public class FfmpegProcessRegistry(
    IOptions<RadioOptions> radioOptions,
    ILogger<FfmpegProcessRegistry> logger)
{
    private readonly object _lock = new();
    private readonly Dictionary<int, long> _tracked = []; // pid → start time (UTC file time)

    private string PidFilePath => Path.Combine(radioOptions.Value.DataRoot, "run", "ffmpeg.pids");

    public void Register(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            lock (_lock)
            {
                _tracked[process.Id] = process.StartTime.ToFileTimeUtc();
                Persist();
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Unregister(process.Id);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not register ffmpeg pid {Pid}", SafePid(process));
        }
    }

    public void Unregister(int pid)
    {
        lock (_lock)
        {
            if (_tracked.Remove(pid))
            {
                Persist();
            }
        }
    }

    /// <summary>Kills surviving ffmpeg processes recorded by a previous run.
    /// Identity is verified by process name AND recorded start time, so a
    /// reused PID can never hit an unrelated process.</summary>
    public void KillOrphansFromPreviousRun()
    {
        string[] lines;
        try
        {
            if (!File.Exists(PidFilePath))
            {
                return;
            }

            lines = File.ReadAllLines(PidFilePath);
        }
        catch
        {
            return;
        }

        var killed = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var pid)
                || !long.TryParse(parts[1], out var startTicks))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.ProcessName.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ageDelta = Math.Abs(process.StartTime.ToFileTimeUtc() - startTicks);
                if (ageDelta > TimeSpan.FromSeconds(2).Ticks)
                {
                    continue; // PID reused by a different ffmpeg — not ours
                }

                process.Kill(entireProcessTree: true);
                killed++;
                logger.LogWarning("Killed orphaned ffmpeg from previous run (PID {Pid})", pid);
            }
            catch (ArgumentException)
            {
                // process already gone — the good case
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Orphan check failed for PID {Pid}", pid);
            }
        }

        try
        {
            File.Delete(PidFilePath);
        }
        catch
        {
            // best effort
        }

        if (killed > 0)
        {
            logger.LogInformation("Startup cleanup: {Count} orphaned ffmpeg process(es) removed", killed);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            File.WriteAllLines(PidFilePath, _tracked.Select(kv => $"{kv.Key};{kv.Value}"));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist ffmpeg pidfile");
        }
    }

    private static string SafePid(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch
        {
            return "?";
        }
    }
}
