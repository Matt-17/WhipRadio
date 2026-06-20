using Microsoft.AspNetCore.SignalR.Client;

namespace WhipRadio.Web.Services;

/// <summary>SignalR invalidation for news and weather production pages.</summary>
public class ProductionLiveClient(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ProductionLiveClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HubConnection? connection;
    private bool started;
    private bool disposed;

    public event Action? NewsChanged;

    public event Action? WeatherChanged;

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

            connection.On("NewsProductionChanged", () => NewsChanged?.Invoke());
            connection.On("WeatherProductionChanged", () => WeatherChanged?.Invoke());

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
                        NewsChanged?.Invoke();
                        WeatherChanged?.Invoke();
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
                logger.LogWarning(ex, "SignalR production connect failed; falling back to snapshot only");
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
