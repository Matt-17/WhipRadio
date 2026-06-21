using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Polls the Icecast <c>status-json.xsl</c> endpoint on a background timer and
/// caches the current/peak listener counts so the
/// <c>whipradio.icecast.listeners</c> observable gauge can read them on scrape
/// without issuing an HTTP call from the metric callback.
/// </summary>
/// <remarks>
/// Failures (Icecast down, parse error) leave the last good values in place and
/// log at debug — a dead Icecast is already surfaced by the health check; this
/// probe must not spam the logs on top.
/// </remarks>
public sealed class IcecastListenerProbe : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IcecastOptions _icecast;
    private readonly ILogger<IcecastListenerProbe> _logger;
    private int _listeners;
    private int _listenerPeak;

    public IcecastListenerProbe(
        IHttpClientFactory httpClientFactory,
        IOptions<IcecastOptions> icecastOptions,
        ILogger<IcecastListenerProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _icecast = icecastOptions.Value;
        _logger = logger;
    }

    /// <summary>Current listener count on the configured mount (last successful poll).</summary>
    public int Listeners => Volatile.Read(ref _listeners);

    /// <summary>Peak listener count since this process started.</summary>
    public int ListenerPeak => Volatile.Read(ref _listenerPeak);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't block startup: the probe is best-effort and the gauge returns 0
        // until the first successful poll.
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }, stoppingToken);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("icecast-admin");
            var status = await client.GetFromJsonAsync<IcecastStatus>(
                $"http://{_icecast.Host}:{_icecast.Port}/status-json.xsl", JsonOptions, ct);
            var listeners = status?.IceStats?.Source?.Listeners ?? 0;
            var peak = status?.IceStats?.Source?.ListenerPeak ?? 0;

            Volatile.Write(ref _listeners, listeners);
            // Peak only ever grows; preserve the process-wide max even across
            // Icecast restarts (a fresh Icecast reports a low peak).
            var currentPeak = Volatile.Read(ref _listenerPeak);
            if (peak > currentPeak)
            {
                Volatile.Write(ref _listenerPeak, peak);
            }
            else if (listeners > currentPeak)
            {
                Volatile.Write(ref _listenerPeak, listeners);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Icecast listener poll failed for {Host}:{Port}", _icecast.Host, _icecast.Port);
        }
    }

    private sealed record IcecastStatus([property: JsonPropertyName("icestats")] IceStats? IceStats);

    private sealed record IceStats([property: JsonPropertyName("source")] IcecastSource? Source);

    private sealed record IcecastSource(
        [property: JsonPropertyName("listeners")] int Listeners,
        [property: JsonPropertyName("listener_peak")] int ListenerPeak);
}
