using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Audio;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// IPcmSampleReader over a short-lived ffmpeg decoder: a filler thread pumps
/// stdout into a 1-second ring buffer so the mixer's master clock never blocks
/// on decode hiccups. Source offsets seek 0.5 s early via ffmpeg -ss (input
/// seek is imprecise) and discard samples to the exact position.
/// </summary>
public sealed class FfmpegPcmSampleReader : IPcmSampleReader, IDisposable
{
    private readonly Process _process;
    private readonly Thread _filler;
    private readonly byte[] _ring;
    private readonly object _lock = new();
    private int _head; // write position
    private int _tail; // read position
    private int _count; // bytes available
    private long _discardBytes;
    private volatile bool _eof;
    private volatile bool _disposed;

    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _eof && _count == 0;
            }
        }
    }

    private readonly FfmpegProcessRegistry? _registry;
    private readonly ILogger<FfmpegPcmSampleReader>? _logger;

    public FfmpegPcmSampleReader(
        string ffmpegPath, string absolutePath, PcmFormat format,
        double startAtSeconds = 0, FfmpegProcessRegistry? registry = null,
        ILogger<FfmpegPcmSampleReader>? logger = null)
    {
        _registry = registry;
        _logger = logger;
        var bytesPerSecond = format.SampleRate * format.Channels * 2;
        _ring = new byte[bytesPerSecond]; // 1 s capacity

        // -ss before -i = fast input seek, but keyframe-imprecise: aim 0.5 s
        // early and discard the difference sample-exactly.
        var seekPre = Math.Max(0, startAtSeconds - 0.5);
        _discardBytes = (long)Math.Round((startAtSeconds - seekPre) * format.SampleRate)
            * format.Channels * 2;

        var seekArg = seekPre > 0
            ? $"-ss {seekPre.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} "
            : "";
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error {seekArg}-i \"{absolutePath}\" "
                    + $"-f s16le -ar {format.SampleRate} -ac {format.Channels} pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _process.Start();
        _registry?.Register(_process);

        _filler = new Thread(FillLoop) { IsBackground = true, Name = "pcm-filler" };
        _filler.Start();
    }

    private void FillLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        var chunk = new byte[16 * 1024];
        try
        {
            while (!_disposed)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    break;
                }

                var offset = 0;
                if (_discardBytes > 0)
                {
                    var discard = (int)Math.Min(_discardBytes, read);
                    _discardBytes -= discard;
                    offset = discard;
                    if (offset >= read)
                    {
                        continue;
                    }
                }

                WriteToRing(chunk, offset, read - offset);
            }
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                try
                {
                    _process.WaitForExit(100);
                    var stderr = _process.StandardError.ReadToEnd();
                    _logger.LogWarning(ex,
                        "FfmpegPcmSampleReader decoder exited ({ExitCode}) for \"{Path}\": {Stderr}",
                        _process.ExitCode, _process.StartInfo.Arguments, stderr);
                }
                catch
                {
                    _logger.LogWarning(ex,
                        "FfmpegPcmSampleReader decoder failed for \"{Path}\"",
                        _process.StartInfo.Arguments);
                }
            }
        }
        finally
        {
            _eof = true;
        }
    }

    private void WriteToRing(byte[] data, int offset, int length)
    {
        var written = 0;
        while (written < length && !_disposed)
        {
            lock (_lock)
            {
                var free = _ring.Length - _count;
                if (free > 0)
                {
                    var n = Math.Min(free, length - written);
                    var first = Math.Min(n, _ring.Length - _head);
                    Array.Copy(data, offset + written, _ring, _head, first);
                    if (n > first)
                    {
                        Array.Copy(data, offset + written + first, _ring, 0, n - first);
                    }

                    _head = (_head + n) % _ring.Length;
                    _count += n;
                    written += n;
                    continue;
                }
            }

            Thread.Sleep(5); // ring full — decoder is way ahead of realtime
        }
    }

    public int Read(Span<short> frame)
    {
        var wantedBytes = frame.Length * 2;
        lock (_lock)
        {
            var available = Math.Min(_count, wantedBytes) & ~1; // whole shorts only
            if (available == 0)
            {
                return 0;
            }

            var first = Math.Min(available, _ring.Length - _tail);
            var target = System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame);
            _ring.AsSpan(_tail, first).CopyTo(target);
            if (available > first)
            {
                _ring.AsSpan(0, available - first).CopyTo(target[first..]);
            }

            _tail = (_tail + available) % _ring.Length;
            _count -= available;
            return available / 2;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            var pid = _process.Id;
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _registry?.Unregister(pid);
        }
        catch
        {
            // already gone
        }

        _process.Dispose();
    }
}
