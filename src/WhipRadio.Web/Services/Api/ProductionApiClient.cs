using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Content production: music jobs, news packages and feeds,
/// conversations, podcast shows, and weather.</summary>
public sealed class ProductionApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<ProductionApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<MusicProductionStatusDto?> GetMusicProductionStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<MusicProductionStatusDto>("/api/music/status", ct);

    public Task<bool> CancelMusicProductionAsync(CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, "/api/music/cancel", null, ct);

    public async Task<NewsProductionDto?> GetNewsProductionAsync(CancellationToken ct = default)
        => await SafeGetAsync<NewsProductionDto>("/api/production/news", ct);

    public Task<string?> SaveNewsProductionSettingsAsync(
        SaveNewsProductionSettingsDto settings,
        CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Put, "/api/production/news/settings", settings, ct);

    public Task<(NewsPackageDto? Package, string? Error)> CreateNextNewsPackageAsync(CancellationToken ct = default)
        => PostLongTupleAsync<NewsPackageDto>("/api/production/news/packages/next", ct);

    public Task<(NewsPackageDto? Package, string? Error)> RecreateNewsPackageAsync(
        Guid id,
        CancellationToken ct = default)
        => PostLongTupleAsync<NewsPackageDto>($"/api/production/news/packages/{id}/recreate", ct);

    public Task<(NewsFeedDto? Feed, string? Error)> CreateNewsFeedAsync(
        SaveNewsFeedDto request,
        CancellationToken ct = default)
        => SendForAsync<NewsFeedDto>(HttpMethod.Post, "/api/news/feeds", request, ct);

    public Task<(NewsFeedDto? Feed, string? Error)> UpdateNewsFeedAsync(
        Guid id,
        SaveNewsFeedDto request,
        CancellationToken ct = default)
        => SendForAsync<NewsFeedDto>(HttpMethod.Put, $"/api/news/feeds/{id}", request, ct);

    public async Task<NewsFeedDto?> ToggleNewsFeedAsync(Guid id, CancellationToken ct = default)
    {
        var (feed, _) = await SendForAsync<NewsFeedDto>(HttpMethod.Post, $"/api/news/feeds/{id}/toggle", null, ct);
        return feed;
    }

    public Task<string?> DeleteNewsFeedAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Delete, $"/api/news/feeds/{id}", null, ct);

    public async Task<List<ConversationSegmentDto>> GetConversationsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ConversationSegmentDto>>("/api/conversations", ct) ?? [];

    public async Task<ConversationSegmentDto?> GetConversationAsync(Guid id, CancellationToken ct = default)
        => await SafeGetAsync<ConversationSegmentDto>($"/api/conversations/{id}", ct);

    public Task<(ConversationSegmentDto? Segment, string? Error)> CreateConversationAsync(
        CreateConversationRequestDto request,
        CancellationToken ct = default)
        => SendForAsync<ConversationSegmentDto>(HttpMethod.Post, "/api/conversations", request, ct);

    public Task<string?> AirConversationNextAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Post, $"/api/conversations/{id}/air-next", null, ct);

    public Task<string?> RetryConversationAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Post, $"/api/conversations/{id}/retry", null, ct);

    public Task<string?> DeleteConversationAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Delete, $"/api/conversations/{id}", null, ct);

    public async Task<List<ConversationSpeakerOptionDto>> GetConversationSpeakersAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ConversationSpeakerOptionDto>>("/api/conversations/speakers", ct) ?? [];

    public async Task<List<PodcastShowDto>> GetPodcastShowsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<PodcastShowDto>>("/api/podcast-shows", ct) ?? [];

    public Task<(PodcastShowDto? Show, string? Error)> CreatePodcastShowAsync(
        SavePodcastShowDto request,
        CancellationToken ct = default)
        => SendForAsync<PodcastShowDto>(HttpMethod.Post, "/api/podcast-shows", request, ct);

    public Task<(PodcastShowDto? Show, string? Error)> UpdatePodcastShowAsync(
        Guid id,
        SavePodcastShowDto request,
        CancellationToken ct = default)
        => SendForAsync<PodcastShowDto>(HttpMethod.Put, $"/api/podcast-shows/{id}", request, ct);

    public async Task<PodcastShowDto?> TogglePodcastShowAsync(Guid id, CancellationToken ct = default)
    {
        var (show, _) = await SendForAsync<PodcastShowDto>(HttpMethod.Post, $"/api/podcast-shows/{id}/toggle", null, ct);
        return show;
    }

    public Task<string?> DeletePodcastShowAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Delete, $"/api/podcast-shows/{id}", null, ct);

    public async Task<WeatherProductionDto?> GetWeatherProductionAsync(CancellationToken ct = default)
        => await SafeGetAsync<WeatherProductionDto>("/api/production/weather", ct);

    public Task<(WeatherProductionDto? Weather, string? Error)> SaveWeatherProductionAsync(
        SaveWeatherProductionSettingsDto request,
        CancellationToken ct = default)
        => SendForAsync<WeatherProductionDto>(HttpMethod.Put, "/api/production/weather", request, ct);

    /// <summary>News package production runs for many minutes on the long client and,
    /// unlike creation flows, propagates failures as the raw error body without a
    /// friendly-timeout catch (matching the pre-split behavior).</summary>
    private async Task<(T? Value, string? Error)> PostLongTupleAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await LongClient.PostAsync(url, null, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<T>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }
}
