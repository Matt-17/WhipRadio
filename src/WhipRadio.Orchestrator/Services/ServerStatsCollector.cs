using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Api;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Technical host metrics for the admin: CPU (Windows GetSystemTimes / Linux
/// /proc/stat deltas), memory, GPU via nvidia-smi, disk space and the on-disk
/// footprint of tracks, talks and the database. Folder sizes are cached for
/// 30 s; CPU usage is a delta between consecutive calls.
/// </summary>
public class ServerStatsCollector(
    IOptions<RadioOptions> radioOptions,
    TimeProvider timeProvider,
    ILogger<ServerStatsCollector> logger)
{
    private readonly Lock _cpuLock = new();
    private (long Idle, long Total)? _lastCpuSample;
    private double _lastCpuPercent;

    private readonly Lock _storageLock = new();
    private IReadOnlyList<StorageAreaDto> _storageCache = [];
    private DateTimeOffset _storageCachedAt = DateTimeOffset.MinValue;

    public async Task<ServerStatsDto> CollectAsync(CancellationToken ct)
    {
        var (memoryTotalMb, memoryUsedMb) = GetMemory();
        var process = Process.GetCurrentProcess();
        var dataRoot = radioOptions.Value.DataRoot;

        double diskTotalGb = 0, diskFreeGb = 0;
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(dataRoot));
            diskTotalGb = drive.TotalSize / 1024.0 / 1024 / 1024;
            diskFreeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disk info unavailable for {Root}", dataRoot);
        }

        return new ServerStatsDto(
            OsDescription: RuntimeInformation.OSDescription,
            ProcessorCount: Environment.ProcessorCount,
            CpuUsagePercent: GetCpuUsagePercent(),
            MemoryTotalMb: memoryTotalMb,
            MemoryUsedMb: memoryUsedMb,
            ProcessMemoryMb: process.WorkingSet64 / 1024.0 / 1024,
            ProcessUptimeSeconds: (DateTime.Now - process.StartTime).TotalSeconds,
            Gpu: await TryGetGpuAsync(ct),
            DataRootPath: Path.GetFullPath(dataRoot),
            DiskTotalGb: diskTotalGb,
            DiskFreeGb: diskFreeGb,
            StorageAreas: GetStorageAreas(dataRoot));
    }

    // --- CPU ---------------------------------------------------------------------

    private double GetCpuUsagePercent()
    {
        try
        {
            var sample = OperatingSystem.IsWindows() ? SampleCpuWindows() : SampleCpuLinux();
            if (sample is null)
            {
                return 0;
            }

            lock (_cpuLock)
            {
                if (_lastCpuSample is { } last && sample.Value.Total > last.Total)
                {
                    var totalDelta = sample.Value.Total - last.Total;
                    var idleDelta = sample.Value.Idle - last.Idle;
                    _lastCpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
                }

                _lastCpuSample = sample;
                return Math.Round(_lastCpuPercent, 1);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "CPU sampling failed");
            return 0;
        }
    }

    private static (long Idle, long Total)? SampleCpuWindows()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time includes idle time.
        var total = kernel + user;
        return (idle, total);
    }

    private static (long Idle, long Total)? SampleCpuLinux()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault();
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = parts.Skip(1).Select(long.Parse).ToArray();
        var idle = values.ElementAtOrDefault(3) + values.ElementAtOrDefault(4); // idle + iowait
        return (idle, values.Sum());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    // --- Memory ------------------------------------------------------------------

    private (double TotalMb, double UsedMb) GetMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    var total = status.ullTotalPhys / 1024.0 / 1024;
                    return (total, total - status.ullAvailPhys / 1024.0 / 1024);
                }
            }
            else if (File.Exists("/proc/meminfo"))
            {
                var lines = File.ReadAllLines("/proc/meminfo");
                var total = ParseMeminfoKb(lines, "MemTotal:") / 1024.0;
                var available = ParseMeminfoKb(lines, "MemAvailable:") / 1024.0;
                return (total, total - available);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Memory sampling failed");
        }

        return (0, 0);
    }

    private static long ParseMeminfoKb(string[] lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
        return line is null
            ? 0
            : long.Parse(line[key.Length..].Replace("kB", string.Empty).Trim(), CultureInfo.InvariantCulture);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    // --- GPU ---------------------------------------------------------------------

    private async Task<GpuStatsDto?> TryGetGpuAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var parts = output.Split('\n')[0].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
            {
                return null;
            }

            var memoryTotalMb = double.Parse(parts[3], CultureInfo.InvariantCulture);
            var memoryUsedMb = NormalizeGpuMemoryUsedMb(
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                memoryTotalMb);

            if (memoryUsedMb is null)
            {
                memoryUsedMb = await TryGetWindowsDedicatedGpuMemoryUsedMbAsync(memoryTotalMb, ct);
            }

            return new GpuStatsDto(
                parts[0],
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                memoryUsedMb,
                memoryTotalMb,
                double.Parse(parts[4], CultureInfo.InvariantCulture));
        }
        catch
        {
            return null; // no NVIDIA GPU / nvidia-smi not on PATH
        }
    }

    internal static double? NormalizeGpuMemoryUsedMb(double value, double totalMb)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            return null;
        }

        if (!double.IsFinite(totalMb) || totalMb <= 0)
        {
            return value;
        }

        if (value <= totalMb * 1.05)
        {
            return Math.Min(value, totalMb);
        }

        var mibFromBytes = value / 1024.0 / 1024;
        if (mibFromBytes <= totalMb * 1.05)
        {
            return Math.Min(mibFromBytes, totalMb);
        }

        var mibFromKib = value / 1024.0;
        if (mibFromKib <= totalMb * 1.05)
        {
            return Math.Min(mibFromKib, totalMb);
        }

        return null;
    }

    private static async Task<double?> TryGetWindowsDedicatedGpuMemoryUsedMbAsync(double totalMb, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !double.IsFinite(totalMb) || totalMb <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "typeperf",
                Arguments = "\"\\GPU Adapter Memory(*)\\Dedicated Usage\" -sc 1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return ParseWindowsDedicatedGpuMemoryUsedMb(output, totalMb);
        }
        catch
        {
            return null;
        }
    }

    internal static double? ParseWindowsDedicatedGpuMemoryUsedMb(string output, double totalMb)
    {
        if (string.IsNullOrWhiteSpace(output) || !double.IsFinite(totalMb) || totalMb <= 0)
        {
            return null;
        }

        var totalBytes = totalMb * 1024.0 * 1024;
        var maxBytes = totalBytes * 1.05;
        double? bestBytes = null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = SplitCsvLine(line);
            foreach (var cell in cells.Skip(1))
            {
                if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes)
                    || bytes <= 0
                    || bytes > maxBytes)
                {
                    continue;
                }

                bestBytes = bestBytes is null ? bytes : Math.Max(bestBytes.Value, bytes);
            }
        }

        return bestBytes is null
            ? null
            : Math.Min(bestBytes.Value, totalBytes) / 1024.0 / 1024;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    // --- Storage footprint ----------------------------------------------------------

    private IReadOnlyList<StorageAreaDto> GetStorageAreas(string dataRoot)
    {
        lock (_storageLock)
        {
            if (timeProvider.GetUtcNow() - _storageCachedAt < TimeSpan.FromSeconds(30))
            {
                return _storageCache;
            }

            var areas = new List<StorageAreaDto>
            {
                MeasureDirectory("Music library", Path.Combine(dataRoot, "library", "tracks")),
                MeasureDirectory("Talks", Path.Combine(dataRoot, "library", "announcements")),
                MeasureDirectory("Database", Path.Combine(dataRoot, "db")),
            };
            areas.Add(new StorageAreaDto("Total data", areas.Sum(a => a.SizeMb), areas.Sum(a => a.FileCount)));

            _storageCache = areas;
            _storageCachedAt = timeProvider.GetUtcNow();
            return areas;
        }
    }

    private static StorageAreaDto MeasureDirectory(string name, string path)
    {
        if (!Directory.Exists(path))
        {
            return new StorageAreaDto(name, 0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
            count++;
        }

        return new StorageAreaDto(name, Math.Round(bytes / 1024.0 / 1024, 1), count);
    }
}
