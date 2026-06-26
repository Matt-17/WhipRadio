namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The mixer's view of the encoder it feeds: an exit flag the loop polls so it
/// can bail when ffmpeg dies. Extracted from a direct <c>Process</c> dependency
/// so the real-time mixer loop can be driven in tests with a controllable
/// "encoder still alive" flag. In production <see cref="ProcessFfmpegHandle"/>
/// satisfies this directly.
/// </summary>
public interface IMixerEncoderSink
{
    bool HasExited { get; }

    int ExitCode { get; }
}
