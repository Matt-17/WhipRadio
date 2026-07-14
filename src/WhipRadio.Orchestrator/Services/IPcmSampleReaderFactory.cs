using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Helpers;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Creates the PCM sample reader the mixer drives for a given playout item.
/// Extracted as a seam so the real-time mixer state machine can be exercised
/// in tests without spawning ffmpeg or reading audio files.
/// </summary>
public interface IPcmSampleReaderFactory
{
    IPcmSampleReader Create(PlayoutItem item, PcmFormat format, double startAtSeconds);
}

/// <summary>Default implementation: a short-lived ffmpeg decoder per item.</summary>
public sealed class FfmpegPcmSampleReaderFactory(
    IOptions<StreamOptions> streamOptions,
    IOptions<RadioOptions> radioOptions,
    FfmpegProcessRegistry registry,
    ILogger<FfmpegPcmSampleReader> logger) : IPcmSampleReaderFactory
{
    public IPcmSampleReader Create(PlayoutItem item, PcmFormat format, double startAtSeconds)
    {
        var absolutePath = MediaPaths.ResolveAbsolute(radioOptions.Value.DataRoot, item.FilePath);
        return new FfmpegPcmSampleReader(
            streamOptions.Value.FfmpegPath, absolutePath, format, startAtSeconds, registry, logger);
    }
}
