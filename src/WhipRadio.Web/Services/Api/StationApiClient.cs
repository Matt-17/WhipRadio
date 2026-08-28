using System.Net;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Now playing, queue, station status, play log, stats, server health,
/// media cleanup, listener greetings, and admin triggers.</summary>
public sealed class StationApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<StationApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<NowPlayingDto?> GetNowPlayingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync("/api/nowplaying", ct);
            if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<NowPlayingDto>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Orchestrator not reachable yet");
            return null;
        }
    }

    public async Task<List<QueueItemDto>> GetQueueAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<QueueItemDto>>("/api/queue", ct) ?? [];

    public async Task<StationStatusDto?> GetStationStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<StationStatusDto>("/api/station/status", ct);

    public async Task<List<PlayLogEntryDto>> GetPlayLogAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>("/api/playlog", ct) ?? [];

    public async Task<StatsDto?> GetStatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<StatsDto>("/api/stats", ct);

    public async Task<List<ConsoleLineDto>> GetConsoleAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ConsoleLineDto>>("/api/console", ct) ?? [];

    public async Task<VoteResultDto?> VoteAsync(Guid trackId, int upDelta, int downDelta, CancellationToken ct = default)
    {
        var (result, _) = await SendForAsync<VoteResultDto>(
            HttpMethod.Post, "/api/votes", new VoteRequestDto(trackId, upDelta, downDelta), ct);
        return result;
    }

    public async Task<ServerStatsDto?> GetServerStatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<ServerStatsDto>("/api/serverstats", ct);

    public async Task<MediaCleanupStatusDto?> GetMediaCleanupStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<MediaCleanupStatusDto>("/api/server/media-cleanup", ct);

    public async Task<MediaCleanupPlanDto?> GetMediaCleanupPlanAsync(CancellationToken ct = default)
        => await SafeGetAsync<MediaCleanupPlanDto>("/api/server/media-cleanup/preview", ct);

    public async Task<(MediaCleanupStatusDto? Status, string? Error)> StartOrphanMediaCleanupAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendForAsync<MediaCleanupStatusDto>(HttpMethod.Post, "/api/server/media-cleanup", null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogDebug(ex, "Orphan media cleanup failed");
            return (null, "Orchestrator not reachable.");
        }
    }

    public async Task<(bool Ok, string Message)> SubmitGreetingAsync(SubmitGreetingDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync("/api/greetings/", request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => (true, "Your message is in the queue!"),
                HttpStatusCode.TooManyRequests => (false, "Easy there — try again a bit later."),
                HttpStatusCode.Forbidden => (false, "Greetings are currently disabled."),
                _ => (false, "Something went wrong — try again."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogWarning(ex, "Greeting submission failed (orchestrator unreachable?)");
            return (false, "The studio isn't answering — try again in a moment.");
        }
    }

    public async Task<PagedListenerMessagesDto> GetGreetingsAsync(
        int page = 1, int pageSize = 25, string? kind = null, CancellationToken ct = default)
    {
        var url = $"/api/greetings/?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(kind))
        {
            url += $"&kind={Uri.EscapeDataString(kind)}";
        }

        return await SafeGetAsync<PagedListenerMessagesDto>(url, ct) ?? new PagedListenerMessagesDto(0, []);
    }

    public async Task QueueGreetingAsync(Guid id, CancellationToken ct = default)
        => await Http.PostAsync($"/api/greetings/{id}/queue", null, ct);

    public async Task DismissGreetingAsync(Guid id, CancellationToken ct = default)
        => await Http.PostAsync($"/api/greetings/{id}/dismiss", null, ct);

    public async Task RunDirectorAsync(CancellationToken ct = default)
        => await Http.PostAsync("/api/admin/director/run", null, ct);
}
