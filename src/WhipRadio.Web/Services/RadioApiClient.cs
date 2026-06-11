using System.Net;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Typed client for the Orchestrator's /api endpoints (via service discovery).</summary>
public class RadioApiClient(HttpClient http, ILogger<RadioApiClient> logger)
{
    public Uri? BaseAddress => http.BaseAddress;

    public async Task<NowPlayingDto?> GetNowPlayingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("/api/nowplaying", ct);
            if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<NowPlayingDto>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Orchestrator not reachable yet");
            return null;
        }
    }

    public async Task<List<QueueItemDto>> GetQueueAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<QueueItemDto>>("/api/queue", ct) ?? [];

    public async Task<List<TrackDto>> GetLibraryAsync(
        string? sort = null, string? genre = null, Guid? artistId = null, CancellationToken ct = default)
    {
        var url = $"/api/library?sort={sort}";
        if (!string.IsNullOrEmpty(genre))
        {
            url += $"&genre={Uri.EscapeDataString(genre)}";
        }

        if (artistId is not null)
        {
            url += $"&artistId={artistId}";
        }

        return await SafeGetAsync<List<TrackDto>>(url, ct) ?? [];
    }

    public async Task<List<ArtistDto>> GetArtistsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ArtistDto>>("/api/artists", ct) ?? [];

    public async Task<List<PlayLogEntryDto>> GetPlayLogAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>("/api/playlog", ct) ?? [];

    public async Task<List<ModeratorDto>> GetModeratorsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ModeratorDto>>("/api/moderators", ct) ?? [];

    public async Task<ModeratorDto?> CreateModeratorAsync(CreateModeratorDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/moderators", request, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ModeratorDto>(ct)
            : null;
    }

    public async Task ToggleModeratorAsync(int id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/moderators/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<PlayLogEntryDto>> GetModeratorTalksAsync(int id, CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>($"/api/moderators/{id}/talks", ct) ?? [];

    public string AnnouncementAudioUrl(Guid id) => $"{BaseAddress?.ToString().TrimEnd('/')}/api/announcements/{id}/audio";

    public async Task<StationSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
        => await SafeGetAsync<StationSettingsDto>("/api/settings", ct);

    public async Task<StationSettingsDto?> SaveSettingsAsync(StationSettingsDto settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/settings", settings, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StationSettingsDto>(ct);
    }

    public async Task<VoteResultDto?> VoteAsync(Guid trackId, int direction, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/votes", new VoteRequestDto(trackId, direction), ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<VoteResultDto>(ct)
            : null;
    }

    public async Task<List<FormatDto>> GetFormatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<FormatDto>>("/api/formats", ct) ?? [];

    public async Task ToggleFormatAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/formats/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task VoteFormatAsync(Guid id, int direction, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/formats/{id}/vote?direction={direction}", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<ProgramSlotDto>> GetScheduleAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ProgramSlotDto>>("/api/schedule", ct) ?? [];

    public async Task<StatsDto?> GetStatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<StatsDto>("/api/stats", ct);

    public async Task<List<ConsoleLineDto>> GetConsoleAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ConsoleLineDto>>("/api/console", ct) ?? [];

    public async Task<(bool Ok, string Message)> SubmitGreetingAsync(SubmitGreetingDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/api/greetings/", request, ct);
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
            logger.LogWarning(ex, "Greeting submission failed (orchestrator unreachable?)");
            return (false, "The studio isn't answering — try again in a moment.");
        }
    }

    public async Task<List<ListenerMessageDto>> GetGreetingsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ListenerMessageDto>>("/api/greetings/", ct) ?? [];

    public async Task QueueGreetingAsync(Guid id, CancellationToken ct = default)
        => await http.PostAsync($"/api/greetings/{id}/queue", null, ct);

    public async Task DismissGreetingAsync(Guid id, CancellationToken ct = default)
        => await http.PostAsync($"/api/greetings/{id}/dismiss", null, ct);

    public async Task RunDirectorAsync(CancellationToken ct = default)
        => await http.PostAsync("/api/admin/director/run", null, ct);

    public async Task<ServerStatsDto?> GetServerStatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<ServerStatsDto>("/api/serverstats", ct);

    private async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            logger.LogDebug(ex, "GET {Url} failed (orchestrator starting?)", url);
            return null;
        }
    }
}
