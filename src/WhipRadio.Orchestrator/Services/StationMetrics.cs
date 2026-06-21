using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Station-specific OpenTelemetry meters: the signals an operator needs at 3am
/// that the default ASP.NET/HTTP/runtime instrumentation doesn't provide.
///
/// Implemented behind <see cref="IStationMetrics"/> so the real-time mixer state
/// machine (and any other unit-tested service) can be constructed with a no-op
/// implementation and stay free of DI ceremony. Production wiring registers
/// <see cref="StationMetrics"/> as a singleton in Program.cs.
///
/// The meter name "WhipRadio" is registered with OTel in ServiceDefaults so the
/// instruments are exported alongside the framework metrics.
/// </summary>
public interface IStationMetrics
{
    void EncoderRestarted();

    void GenerationStarted(string kind);

    void GenerationSucceeded(string kind, TimeSpan elapsed);

    void GenerationFailed(string kind);

    void MixerTransition(string strategy, int clips);
}

/// <summary>
/// No-op implementation for tests and services that don't need metrics. Avoids
/// null-checks at every call site and keeps the mixer test harness untouched.
/// </summary>
public sealed class NullStationMetrics : IStationMetrics
{
    public static readonly NullStationMetrics Instance = new();
    public void EncoderRestarted() { }
    public void GenerationStarted(string kind) { }
    public void GenerationSucceeded(string kind, TimeSpan elapsed) { }
    public void GenerationFailed(string kind) { }
    public void MixerTransition(string strategy, int clips) { }
}

/// <summary>
/// Concrete <see cref="IStationMetrics"/> backed by a <see cref="Meter"/> named
/// "WhipRadio". Counters/histograms are created once; observable gauges pull
/// live values from <see cref="IPlayoutQueue"/>, <see cref="MixerDiagnostics"/>,
/// and <see cref="IcecastListenerProbe"/> on each scrape.
/// </summary>
public sealed class StationMetrics : IStationMetrics, IDisposable
{
    public const string MeterName = "WhipRadio";
    public const string MeterVersion = "1.0";

    private readonly Meter _meter;
    private readonly Counter<long> _encoderRestarts;
    private readonly Counter<long> _generationFailures;
    private readonly Histogram<double> _generationLatency;
    private readonly Counter<long> _mixerTransitions;
    private readonly Counter<long> _mixerClips;

    public StationMetrics(
        IPlayoutQueue queue,
        MixerDiagnostics diagnostics,
        IcecastListenerProbe icecastProbe)
    {
        _meter = new Meter(MeterName, MeterVersion);

        _encoderRestarts = _meter.CreateCounter<long>(
            "whipradio.encoder.restarts",
            unit: "{restart}",
            description: "Encoder ffmpeg process restarts (each indicates a crash or forced exit).");

        _generationFailures = _meter.CreateCounter<long>(
            "whipradio.generation.failures",
            unit: "{failure}",
            description: "Content generation cycle failures, by kind (music|announcement|news).");

        _generationLatency = _meter.CreateHistogram<double>(
            "whipradio.generation.latency",
            unit: "s",
            description: "Content generation cycle wall-clock duration, by kind.");

        _mixerTransitions = _meter.CreateCounter<long>(
            "whipradio.mixer.transitions",
            unit: "{transition}",
            description: "Mixer transitions between playout items, by strategy.");

        _mixerClips = _meter.CreateCounter<long>(
            "whipradio.mixer.clips",
            unit: "{clip}",
            description: "Sample-level clips counted during mixer transitions.");

        // Observable gauges: values are read on each scrape via callbacks, so no
        // background thread is needed for queue depth / mixer / listener counts.
        _meter.CreateObservableGauge(
            "whipradio.playout.queue_depth",
            observeValue: () => queue.Count,
            unit: "{item}",
            description: "Items currently queued for playout.");

        _meter.CreateObservableGauge(
            "whipradio.mixer.transitions_this_session",
            observeValue: () => diagnostics.Snapshot().Transitions,
            unit: "{transition}",
            description: "Mixer transitions since the current session engaged.");

        _meter.CreateObservableGauge(
            "whipradio.icecast.listeners",
            observeValue: () => icecastProbe.Listeners,
            unit: "{listener}",
            description: "Current Icecast mount listeners.");

        _meter.CreateObservableGauge(
            "whipradio.icecast.listener_peak",
            observeValue: () => icecastProbe.ListenerPeak,
            unit: "{listener}",
            description: "Peak Icecast mount listeners since process start.");
    }

    public void EncoderRestarted() => _encoderRestarts.Add(1);

    public void GenerationStarted(string kind) { /* latency is measured by the caller via a stopwatch; nothing to record at start. */ }

    public void GenerationSucceeded(string kind, TimeSpan elapsed)
        => _generationLatency.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("kind", kind));

    public void GenerationFailed(string kind)
        => _generationFailures.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void MixerTransition(string strategy, int clips)
    {
        _mixerTransitions.Add(1, new KeyValuePair<string, object?>("strategy", strategy));
        if (clips > 0)
        {
            _mixerClips.Add(clips);
        }
    }

    public void Dispose() => _meter.Dispose();
}
