using Microsoft.AspNetCore.SignalR.Client;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

public class ChatLiveClient(
    RadioApiClient api,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ChatLiveClient> logger) : LiveClientBase(configuration, environment, logger)
{
    public IReadOnlyList<ChatChannelDto> Channels { get; private set; } = [];

    public event Action? Changed;

    public event Action<ChatMessageDto>? MessageAdded;

    public event Action<ChatAgentThinkingDto>? ThinkingChanged;

    protected override void RegisterHandlers(HubConnection connection)
    {
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
    }

    protected override Task RefreshCoreAsync() => RefreshSnapshotAsync();

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
}
