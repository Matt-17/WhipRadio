using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// Restarts hung local studio containers via the Docker CLI. The container is
/// found by the host port the studio URL points at (studios are plain
/// docker-run containers, see start-studios.ps1), so no configuration is
/// needed. Online API studios have no container and are never touched.
/// </summary>
public sealed class StudioDockerControl(ILogger<StudioDockerControl> logger)
{
    /// <summary>Several queued generations time out together when a studio
    /// wedges — one restart fixes all of them, the rest are swallowed.</summary>
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastRestartUtc = new();

    public static bool IsLocalStudio(Studio studio)
        => Uri.TryCreate(studio.Url, UriKind.Absolute, out var uri)
           && (uri.IsLoopback || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase));

    /// <summary>Restarts the container behind the studio. Auto-restarts respect
    /// the cooldown; the manual button on the studios page forces through.</summary>
    public async Task<(bool Ok, string Detail)> TryRestartAsync(
        Studio studio, string reason, bool force, CancellationToken ct)
    {
        if (!IsLocalStudio(studio))
        {
            return (false, "Not a local container studio.");
        }

        if (!force)
        {
            var last = _lastRestartUtc.TryGetValue(studio.Id, out var t) ? t : DateTime.MinValue;
            if (DateTime.UtcNow - last < RestartCooldown)
            {
                return (false, $"Restarted {(DateTime.UtcNow - last).TotalSeconds:F0}s ago — cooldown.");
            }
        }

        var port = new Uri(studio.Url).Port;
        var (psOk, names, psError) = await RunDockerAsync($"ps --filter publish={port} --format {{{{.Names}}}}", ct);
        if (!psOk)
        {
            logger.LogWarning("Container lookup for {Studio} (port {Port}) failed: {Error}", studio.Name, port, psError);
            return (false, $"docker ps failed: {psError}");
        }

        var containerName = names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(containerName))
        {
            return (false, $"No running container publishes port {port}.");
        }

        _lastRestartUtc[studio.Id] = DateTime.UtcNow;
        logger.LogWarning(
            "Restarting container {Container} for {Studio} — {Reason}", containerName, studio.Name, reason);

        var (restartOk, _, restartError) = await RunDockerAsync($"restart {containerName}", ct);
        if (!restartOk)
        {
            logger.LogError("docker restart {Container} failed: {Error}", containerName, restartError);
            return (false, $"docker restart failed: {restartError}");
        }

        logger.LogInformation("Container {Container} restarted (models reload on the next job)", containerName);
        return (true, $"Container {containerName} restarted.");
    }

    private static async Task<(bool Ok, string Output, string Error)> RunDockerAsync(
        string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, "", $"Docker CLI not available: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DockerCommandTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, "", $"docker {arguments.Split(' ')[0]} timed out after {DockerCommandTimeout.TotalSeconds:F0}s.");
        }

        return process.ExitCode == 0
            ? (true, await stdout, await stderr)
            : (false, await stdout, string.IsNullOrWhiteSpace(await stderr) ? $"exit code {process.ExitCode}" : (await stderr).Trim());
    }
}
