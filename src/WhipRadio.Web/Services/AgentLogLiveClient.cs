using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

public class AgentLogLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AgentLogLiveClient> logger) : IAsyncDisposable
{
    private const int MaxEntries = 300;

    private readonly SemaphoreSlim gate = new(1, 1);
    private HubConnection? connection;
    private bool started;
    private bool disposed;

    public IReadOnlyList<AgentLogEntryDto> Entries { get; private set; } = [];

    public event Action? Changed;

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

            await RefreshSnapshotAsync();

            string baseUrl = configuration["services:orchestrator:http:0"]
                ?? configuration["Orchestrator:Endpoint"]
                ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator");

            connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/radio")
                .WithAutomaticReconnect()
                .Build();

            connection.On<AgentLogEntryDto>("AgentActionLogged", entry =>
            {
                PrependEntry(entry);
                Changed?.Invoke();
            });

            connection.Reconnected += async _ => await RefreshSnapshotAsync();
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
                        await RefreshSnapshotAsync();
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
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                await connection.StartAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR agent log connect failed; falling back to snapshot only");
            }

            started = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RefreshSnapshotAsync()
    {
        Entries = await api.GetAgentLogAsync(take: MaxEntries);
        Changed?.Invoke();
    }

    private void PrependEntry(AgentLogEntryDto entry)
    {
        List<AgentLogEntryDto> updated = [entry, .. Entries.Where(existing => existing.Id != entry.Id)];
        if (updated.Count > MaxEntries)
        {
            updated.RemoveRange(MaxEntries, updated.Count - MaxEntries);
        }

        Entries = updated;
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
