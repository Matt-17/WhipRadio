using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Configuration;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Station-specific readiness/liveness probes. Registered in Program.cs with tags
/// so /health (ready) requires the content chain to be reachable while /alive
/// (liveness) stays cheap and only checks the process is responsive.
/// </summary>
/// <remarks>
/// A degraded probe reports <see cref="HealthStatus.Degraded"/> — the station can
/// keep streaming silence or existing library audio, but the operator should be
/// alerted that a dependency is down. Unhealthy is reserved for failures that
/// mean the station cannot serve audio at all.
/// </remarks>
public sealed class IcecastHealthCheck(IHttpClientFactory httpClientFactory, IOptions<IcecastOptions> icecastOptions)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var icecast = icecastOptions.Value;
        var client = httpClientFactory.CreateClient("health-icecast");
        client.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            using var response = await client.GetAsync($"http://{icecast.Host}:{icecast.Port}/", ct);
            if (response.IsSuccessStatusCode || (int)response.StatusCode < 500)
            {
                return HealthCheckResult.Healthy($"Icecast reachable at {icecast.Host}:{icecast.Port}");
            }

            return HealthCheckResult.Unhealthy($"Icecast returned HTTP {response.StatusCode}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Icecast unreachable at {icecast.Host}:{icecast.Port}", ex);
        }
    }
}

public sealed class FfmpegHealthCheck(IOptions<StreamOptions> streamOptions) : IHealthCheck
{
    private readonly Lock _lock = new();
    private bool _cached;
    private DateTime _checkedAtUtc;
    private HealthCheckResult _result = HealthCheckResult.Healthy();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cached && DateTime.UtcNow - _checkedAtUtc < TimeSpan.FromMinutes(1))
            {
                return Task.FromResult(_result);
            }
        }

        var path = streamOptions.Value.FfmpegPath;
        var probe = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        HealthCheckResult result;
        try
        {
            probe.Start();
            var exited = probe.WaitForExit(milliseconds: 3000);
            if (!exited)
            {
                try { probe.Kill(entireProcessTree: true); } catch { }
                result = HealthCheckResult.Degraded($"ffmpeg '{path}' probe timed out");
            }
            else if (probe.ExitCode == 0)
            {
                result = HealthCheckResult.Healthy($"ffmpeg '{path}' present");
            }
            else
            {
                result = HealthCheckResult.Unhealthy($"ffmpeg '{path}' exited with code {probe.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            result = HealthCheckResult.Unhealthy($"ffmpeg binary '{path}' not found/executable", ex);
        }
        finally
        {
            try { probe.Dispose(); } catch { }
        }

        lock (_lock)
        {
            _cached = true;
            _checkedAtUtc = DateTime.UtcNow;
            _result = result;
        }

        return Task.FromResult(result);
    }
}

public sealed class RadioDbHealthCheck(IDbContextFactory<RadioDbContext> dbFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("Radio database connected")
                : HealthCheckResult.Unhealthy("Radio database connection failed");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Radio database unreachable", ex);
        }
    }
}

public sealed class OllamaHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var endpoint = (configuration["Llm:Endpoint"] ?? ServiceEndpointDefaults.WriterRoom).TrimEnd('/');
        var client = httpClientFactory.CreateClient("health-ollama");
        client.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            using var response = await client.GetAsync($"{endpoint}/api/tags", ct);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy($"Ollama writer room reachable at {endpoint}");
            }

            return HealthCheckResult.Degraded($"Ollama returned HTTP {response.StatusCode} at {endpoint}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Content generation stalls but the mount keeps streaming — degraded, not down.
            return HealthCheckResult.Degraded($"Ollama writer room unreachable at {endpoint}", ex);
        }
    }
}

public sealed class EncoderHeartbeatHealthCheck(EncoderHeartbeat heartbeat) : IHealthCheck
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var age = DateTime.UtcNow - heartbeat.LastBeatUtc;
        if (age <= StaleThreshold)
        {
            return Task.FromResult(HealthCheckResult.Healthy($"Encoder pumping (last beat {age.TotalSeconds:F0}s ago)"));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"Encoder stalled — no heartbeat for {age.TotalSeconds:F0}s (crash-loop or hung ffmpeg)"));
    }
}
