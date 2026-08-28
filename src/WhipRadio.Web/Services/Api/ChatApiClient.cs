using System.Globalization;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Chat channels and messages, agent log, verbs, and boss approvals.</summary>
public sealed class ChatApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<ChatApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<List<ChatChannelDto>> GetChatChannelsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ChatChannelDto>>("/api/chat/channels", ct) ?? [];

    public async Task<PagedChatMessagesDto> GetChatMessagesAsync(
        Guid channelId,
        DateTime? beforeUtc = null,
        int take = 50,
        CancellationToken ct = default)
    {
        string url = $"/api/chat/channels/{channelId}/messages?take={take}";
        if (beforeUtc is { } before)
        {
            url += $"&before={Uri.EscapeDataString(before.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}";
        }

        return await SafeGetAsync<PagedChatMessagesDto>(url, ct)
            ?? new PagedChatMessagesDto([], false);
    }

    public Task<(ChatMessageDto? Message, string? Error)> PostChatMessageAsync(
        Guid channelId,
        string text,
        CancellationToken ct = default)
        => SendForAsync<ChatMessageDto>(
            HttpMethod.Post, $"/api/chat/channels/{channelId}/messages", new PostChatMessageRequest(text), ct);

    public async Task MarkChatReadAsync(Guid channelId, CancellationToken ct = default)
        => await Http.PostAsync($"/api/chat/channels/{channelId}/read", null, ct);

    public async Task<List<ChatParticipantOptionDto>> GetChatParticipantsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ChatParticipantOptionDto>>("/api/chat/participants", ct) ?? [];

    public Task<(ChatChannelDto? Channel, string? Error)> CreateGroupChatChannelAsync(
        string? name,
        IReadOnlyList<ChatParticipantSelectionDto> members,
        CancellationToken ct = default)
        => SendForAsync<ChatChannelDto>(
            HttpMethod.Post, "/api/chat/channels/group", new CreateGroupChannelRequestDto(name, members), ct);

    public Task<bool> AddChatChannelMemberAsync(
        Guid channelId,
        ChatParticipantSelectionDto selection,
        CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/chat/channels/{channelId}/members", selection, ct);

    public Task<bool> RemoveChatChannelMemberAsync(
        Guid channelId,
        Guid memberId,
        CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Delete, $"/api/chat/channels/{channelId}/members/{memberId}", null, ct);

    public Task<string?> ConfirmChatActionAsync(Guid messageId, int actionIndex, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Post, $"/api/chat/actions/{messageId}/{actionIndex}/confirm", null, ct);

    public Task<string?> DismissChatActionAsync(Guid messageId, int actionIndex, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Post, $"/api/chat/actions/{messageId}/{actionIndex}/dismiss", null, ct);

    public async Task<List<AgentLogEntryDto>> GetAgentLogAsync(
        string? agent = null,
        int take = 200,
        CancellationToken ct = default)
    {
        string url = $"/api/agent-log?take={take}";
        if (!string.IsNullOrWhiteSpace(agent))
        {
            url += $"&agent={Uri.EscapeDataString(agent)}";
        }

        return await SafeGetAsync<List<AgentLogEntryDto>>(url, ct) ?? [];
    }

    public async Task<List<VerbDefinitionDto>> GetVerbsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<VerbDefinitionDto>>("/api/verbs", ct) ?? [];

    public async Task<(InvokeVerbResultDto? Result, string? Error)> InvokeVerbAsync(
        InvokeVerbRequest request,
        CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await Http.PostAsJsonAsync("/api/verbs/invoke", request, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<InvokeVerbResultDto>(ct), null)
                : (null, SingleLine(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Invoke verb {Verb} failed", request.Name);
            return (null, "Orchestrator not reachable.");
        }
    }

    public async Task<List<PendingApprovalDto>> GetApprovalsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PendingApprovalDto>>("/api/approvals", ct) ?? [];

    public async Task<(bool Ok, string? Error)> ApproveApprovalAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await Http.PostAsync($"/api/approvals/{id}/approve", null, ct);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, SingleLine(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Approve {Id} failed", id);
            return (false, "Orchestrator not reachable.");
        }
    }

    public async Task<(bool Ok, string? Error)> DenyApprovalAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await Http.PostAsJsonAsync(
                $"/api/approvals/{id}/deny", new DenyApprovalRequest(reason), ct);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, SingleLine(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Deny {Id} failed", id);
            return (false, "Orchestrator not reachable.");
        }
    }
}
