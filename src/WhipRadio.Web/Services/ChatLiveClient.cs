using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

public class ChatLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ChatLiveClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HubConnection? connection;
    private bool started;
    private bool disposed;

    public IReadOnlyList<ChatChannelDto> Channels { get; private set; } = [];

    public event Action? Changed;

    public event Action<ChatMessageDto>? MessageAdded;

    public event Action<ChatAgentThinkingDto>? ThinkingChanged;

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

            connection.On<ChatMessageDto>("ChatMessageAdded", message =>
            {
                MessageAdded?.Invoke(message);
                Changed?.Invoke();
            });

            connection.On<ChatChannelDto>("ChatChannelUpdated", channel =>
            {
                UpsertChannel(channel);
                Changed?.Invoke();
            });

            connection.On<ChatAgentThinkingDto>("ChatAgentThinking", thinking =>
            {
                ThinkingChanged?.Invoke(thinking);
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
                logger.LogWarning(ex, "SignalR chat connect failed; falling back to snapshot only");
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
        Channels = await api.GetChatChannelsAsync();
        Changed?.Invoke();
    }

    private void UpsertChannel(ChatChannelDto channel)
    {
        List<ChatChannelDto> updated = Channels.Where(existing => existing.Id != channel.Id).ToList();
        updated.Add(channel);

        // Mirror the server's stable rail order so live updates never make the
        // channel list jump around.
        Channels = updated
            .OrderBy(item => item.IsArchived)
            .ThenBy(item => KindRank(item.Kind))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int KindRank(string kind)
        => kind switch
        {
            "Station" => 0,
            "DirectorDm" => 1,
            "HostDm" => 2,
            _ => 3,
        };

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
