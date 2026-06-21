using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

public class ArtistFeedLiveClient(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ArtistFeedLiveClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HubConnection? connection;
    private bool started;
    private bool disposed;

    public event Action<ArtistPostDto>? PostAdded;

    public async Task EnsureStartedAsync()
    {
        if (started)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            if (started)
            {
                return;
            }

            var baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator");

            connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();

            connection.On<ArtistPostDto>("ArtistPostAdded", post => PostAdded?.Invoke(post));

            connection.Closed += async _ =>
            {
                while (!disposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (connection is null)
                        {
                            return;
                        }

                        await connection.StartAsync();
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                    }
                }
            };

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await connection.StartAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR artist feed connect failed; falling back to initial snapshot only");
            }

            started = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        gate.Dispose();
    }
}
