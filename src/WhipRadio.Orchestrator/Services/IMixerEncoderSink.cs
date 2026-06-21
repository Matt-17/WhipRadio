using System.Diagnostics;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The mixer's view of the encoder it feeds: an exit flag + the stdin stream.
/// Extracted from a direct <see cref="Process"/> dependency so the real-time
/// mixer loop can be driven in tests with an in-memory stream and a controllable
/// "encoder still alive" flag.
/// </summary>
public interface IMixerEncoderSink
{
    bool HasExited { get; }

    int ExitCode { get; }
}

/// <summary>Wraps the real ffmpeg encoder process for production use.</summary>
public sealed class ProcessEncoderSink(Process process) : IMixerEncoderSink
{
    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;
}
