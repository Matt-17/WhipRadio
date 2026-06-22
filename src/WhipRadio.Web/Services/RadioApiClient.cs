using System.Net;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>Typed client for the Orchestrator's /api endpoints (via service discovery).</summary>
public sealed record DeleteTrackResult(bool Deleted, bool Deferred, string? Error);

public sealed record DeleteArtistResult(bool Deleted, string? Error);

public class RadioApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<RadioApiClient> logger)
{
    public Uri? BaseAddress => http.BaseAddress;

    /// <summary>Minutes-long calls (voice design): no retry pipeline, 12 min timeout.</summary>
    private HttpClient LongClient => httpClientFactory.CreateClient("orchestrator-long");

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

    public async Task<StationStatusDto?> GetStationStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<StationStatusDto>("/api/station/status", ct);

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

    public async Task<PagedArtistPostsDto> GetArtistPostsAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
        => await SafeGetAsync<PagedArtistPostsDto>($"/api/artist-posts?page={page}&pageSize={pageSize}", ct)
            ?? new PagedArtistPostsDto(0, page, pageSize, []);

    public async Task<ArtistDto?> GetArtistAsync(Guid id, CancellationToken ct = default)
        => await SafeGetAsync<ArtistDto>($"/api/artists/{id}", ct);

    public async Task<(ArtistDto? Artist, string? Error)> CreateArtistAsync(string hint, CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync("/api/artists", new CreateArtistRequestDto(hint), ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ArtistDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Artist creation timed out or the writer room is unreachable.");
        }
    }

    public async Task<(ArtistDto? Artist, string? Error)> RedefineArtistAsync(Guid id, string? hint, CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync(
                $"/api/artists/{id}/redefine",
                new RedefineArtistRequestDto(hint),
                ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ArtistDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Artist redefinition timed out or the writer room is unreachable.");
        }
    }

    public async Task<bool> ProduceTrackForArtistAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/artists/{id}/produce", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeleteArtistResult> DeleteArtistAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/artists/{id}", ct);
        return response.StatusCode == HttpStatusCode.NoContent
            ? new DeleteArtistResult(Deleted: true, Error: null)
            : new DeleteArtistResult(Deleted: false, Error: await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<MusicProductionStatusDto?> GetMusicProductionStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<MusicProductionStatusDto>("/api/music/status", ct);

    public async Task<bool> CancelMusicProductionAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("/api/music/cancel", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeleteTrackResult> DeleteTrackAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/library/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new DeleteTrackResult(Deleted: true, Deferred: false, Error: null);
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new DeleteTrackResult(Deleted: false, Deferred: true, Error: null);
        }

        return new DeleteTrackResult(
            Deleted: false,
            Deferred: false,
            Error: await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Same-origin media proxy URL — browser-safe regardless of scheme/host.</summary>
    public string TrackAudioUrl(Guid id) => $"/media/track/{id}";

    public async Task<List<PlayLogEntryDto>> GetPlayLogAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>("/api/playlog", ct) ?? [];

    public async Task<List<ModeratorDto>> GetModeratorsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ModeratorDto>>("/api/moderators", ct) ?? [];

    public async Task<(ModeratorDto? Moderator, string? Error)> CreateModeratorAsync(
        CreateModeratorDto request,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync("/api/moderators", request, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ModeratorDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Host voice design timed out or the voice booth is unreachable.");
        }
    }

    public async Task<(ModeratorDto? Moderator, string? Error)> CreateSpecialistHostAsync(
        CreateSpecialistHostRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync("/api/moderators/specialist", request, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ModeratorDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Host creation timed out or the writer room / voice booth is unreachable.");
        }
    }

    public async Task ToggleModeratorAsync(int id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/moderators/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ModeratorUsageDto?> GetModeratorUsageAsync(int id, CancellationToken ct = default)
        => await SafeGetAsync<ModeratorUsageDto>($"/api/moderators/{id}/usage", ct);

    public async Task<(FireModeratorResultDto? Result, string? Error)> FireModeratorAsync(
        int id,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/moderators/{id}/fire", content: null, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<FireModeratorResultDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<ModeratorDto?> SetModeratorPhotoAsync(int id, string? photoUrl, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"/api/moderators/{id}/photo", new ModeratorPhotoDto(photoUrl), ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ModeratorDto>(ct)
            : null;
    }

    public async Task<List<PlayLogEntryDto>> GetModeratorTalksAsync(int id, CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>($"/api/moderators/{id}/talks", ct) ?? [];

    /// <summary>Same-origin media proxy URL — browser-safe regardless of scheme/host.</summary>
    public string AnnouncementAudioUrl(Guid id) => $"/media/announcement/{id}";

    public async Task<StationSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
        => await SafeGetAsync<StationSettingsDto>("/api/settings", ct);

    public async Task<BrandingDto?> GetBrandingAsync(CancellationToken ct = default)
        => await SafeGetAsync<BrandingDto>("/api/branding", ct);

    public async Task<BrandingDto?> SaveBrandingAsync(SaveBrandingDto branding, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/branding", branding, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingDto>(ct);
    }

    public async Task<(JingleDto? Jingle, string? Error)> CreateJingleAsync(CreateJingleDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync("/api/jingles", request, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<JingleDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Jingle generation timed out or the studio is unreachable.");
        }
    }

    public async Task<List<JingleDto>> GetJinglesAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<JingleDto>>("/api/jingles", ct) ?? [];

    public async Task<JingleDto?> ToggleJingleAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/jingles/{id}/toggle", null, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JingleDto>(ct)
            : null;
    }

    public async Task<string?> DeleteJingleAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/jingles/{id}", ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public string JingleAudioUrl(Guid id) => $"/media/jingle/{id}";

    public async Task<List<StudioDto>> GetStudiosAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<StudioDto>>("/api/studios", ct) ?? [];

    public async Task<PagedStudioHistoryDto> GetStudioHistoryAsync(
        Guid? studioId = null,
        string? kind = null,
        string? status = null,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (studioId is not null)
        {
            query.Add($"studioId={studioId}");
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query.Add($"kind={Uri.EscapeDataString(kind)}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search)}");
        }

        var url = $"/api/studio-history?{string.Join('&', query)}";
        using var response = await http.GetAsync(url, ct);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<PagedStudioHistoryDto>(ct)
                ?? new PagedStudioHistoryDto(0, []);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"GET {url} returned {(int)response.StatusCode} {response.ReasonPhrase}: {SingleLine(body)}");
    }

    public async Task<StudioTestResultDto?> TestStudioAsync(TestStudioDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/api/studios/test", request, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<StudioTestResultDto>(ct)
                : new StudioTestResultDto(false, null, $"Test endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new StudioTestResultDto(false, null, "Orchestrator not reachable.");
        }
    }

    public async Task<(StudioDto? Studio, string? Error)> CreateStudioAsync(SaveStudioDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/studios", request, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<StudioDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<(StudioDto? Studio, string? Error)> UpdateStudioAsync(Guid id, SaveStudioDto request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"/api/studios/{id}", request, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<StudioDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task ToggleStudioAsync(Guid id, CancellationToken ct = default)
        => await http.PostAsync($"/api/studios/{id}/toggle", null, ct);

    public async Task<StudioRestartResultDto> RestartStudioAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"/api/studios/{id}/restart", null, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<StudioRestartResultDto>(ct)
                    ?? new StudioRestartResultDto(false, "Empty response.")
                : new StudioRestartResultDto(false, $"Restart endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new StudioRestartResultDto(false, "Orchestrator not reachable.");
        }
    }

    public async Task<string?> DeleteStudioAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/studios/{id}", ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<StationSettingsDto?> SaveSettingsAsync(StationSettingsDto settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/settings", settings, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StationSettingsDto>(ct);
    }

    public async Task<NewsProductionDto?> GetNewsProductionAsync(CancellationToken ct = default)
        => await SafeGetAsync<NewsProductionDto>("/api/production/news", ct);

    public async Task<string?> SaveNewsProductionSettingsAsync(
        SaveNewsProductionSettingsDto settings,
        CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/production/news/settings", settings, ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<(NewsPackageDto? Package, string? Error)> CreateNextNewsPackageAsync(CancellationToken ct = default)
    {
        using var response = await LongClient.PostAsync("/api/production/news/packages/next", null, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<NewsPackageDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<(NewsPackageDto? Package, string? Error)> RecreateNewsPackageAsync(
        Guid id,
        CancellationToken ct = default)
    {
        using var response = await LongClient.PostAsync($"/api/production/news/packages/{id}/recreate", null, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<NewsPackageDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<(NewsFeedDto? Feed, string? Error)> CreateNewsFeedAsync(
        SaveNewsFeedDto request,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/news/feeds", request, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<NewsFeedDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<(NewsFeedDto? Feed, string? Error)> UpdateNewsFeedAsync(
        Guid id,
        SaveNewsFeedDto request,
        CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"/api/news/feeds/{id}", request, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<NewsFeedDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<NewsFeedDto?> ToggleNewsFeedAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/api/news/feeds/{id}/toggle", null, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<NewsFeedDto>(ct)
            : null;
    }

    public async Task<string?> DeleteNewsFeedAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/news/feeds/{id}", ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<WeatherProductionDto?> GetWeatherProductionAsync(CancellationToken ct = default)
        => await SafeGetAsync<WeatherProductionDto>("/api/production/weather", ct);

    public async Task<(WeatherProductionDto? Weather, string? Error)> SaveWeatherProductionAsync(
        SaveWeatherProductionSettingsDto request,
        CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/production/weather", request, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<WeatherProductionDto>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
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

    public async Task<PrivacyReportDto?> GetPrivacyAsync(CancellationToken ct = default)
        => await SafeGetAsync<PrivacyReportDto>("/api/privacy", ct);

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
        => await http.PostAsync($"/api/greetings/{id}/queue", null, ct);

    public async Task DismissGreetingAsync(Guid id, CancellationToken ct = default)
        => await http.PostAsync($"/api/greetings/{id}/dismiss", null, ct);

    public async Task RunDirectorAsync(CancellationToken ct = default)
        => await http.PostAsync("/api/admin/director/run", null, ct);

    public async Task<MixerOverviewDto?> GetMixerAsync(CancellationToken ct = default)
        => await SafeGetAsync<MixerOverviewDto>("/api/mixer", ct);

    public async Task<string?> SaveMixerSettingsAsync(MixerSettingsDto settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("/api/mixer/settings", settings, ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    public async Task RunMixerBackfillAsync(CancellationToken ct = default)
        => await http.PostAsync("/api/mixer/backfill", null, ct);

    public async Task<(DesignedVoiceDto? Voice, string? Error)> DesignVoiceAsync(
        DesignVoiceDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsJsonAsync("/api/voices/design", request, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<DesignedVoiceDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Voice design timed out or the studio is unreachable.");
        }
    }

    public async Task<(DesignedVoiceDto? Voice, string? Error)> RedesignVoiceAsync(
        int moderatorId, CancellationToken ct = default)
    {
        try
        {
            using var response = await LongClient.PostAsync($"/api/moderators/{moderatorId}/redesign-voice", null, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<DesignedVoiceDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Voice design timed out or the studio is unreachable.");
        }
    }

    public async Task<bool> ApplyVoiceAsync(int moderatorId, ApplyVoiceDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"/api/moderators/{moderatorId}/apply-voice", request, ct);
        return response.IsSuccessStatusCode;
    }

    public string VoicePreviewUrl(string handle) => $"/media/voice-preview/{Uri.EscapeDataString(handle)}";

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
            using var response = await http.PostAsync("/api/server/media-cleanup", null, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<MediaCleanupStatusDto>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Orphan media cleanup failed");
            return (null, "Orchestrator not reachable.");
        }
    }

    private async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GET {Url} failed (orchestrator starting or returned unexpected data?)", url);
            return null;
        }
    }

    private static string SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No response body.";
        }

        var oneLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 320 ? oneLine : $"{oneLine[..317]}...";
    }
}
