using System.Net;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Typed client for the Orchestrator's /api endpoints (via service discovery).</summary>
public class RadioApiClient(HttpClient http, ILogger<RadioApiClient> logger)
{
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
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Orchestrator not reachable yet");
            return null;
        }
    }

    public async Task<List<TrackDto>> GetLibraryAsync(string? sort = null, CancellationToken ct = default)
        => await SafeGetAsync<List<TrackDto>>($"/api/library?sort={sort}", ct) ?? [];

    public async Task<List<PlayLogEntryDto>> GetPlayLogAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>("/api/playlog", ct) ?? [];

    public async Task<List<ModeratorDto>> GetModeratorsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ModeratorDto>>("/api/moderators", ct) ?? [];

    public async Task ToggleModeratorAsync(int id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/moderators/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

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

    private async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "GET {Url} failed (orchestrator starting?)", url);
            return null;
        }
    }
}
