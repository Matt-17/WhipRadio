using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Hosts (moderators): hiring, specialist creation, usage, firing,
/// photos, talks, and voice design.</summary>
public sealed class ModeratorsApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<ModeratorsApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<List<ModeratorDto>> GetModeratorsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ModeratorDto>>("/api/moderators", ct) ?? [];

    public Task<(ModeratorDto? Moderator, string? Error)> CreateModeratorAsync(
        CreateModeratorDto request,
        CancellationToken ct = default)
        => PostLongForAsync<ModeratorDto>(
            "/api/moderators", request, "Host voice design timed out or the voice booth is unreachable.", ct);

    public Task<(ModeratorDto? Moderator, string? Error)> CreateSpecialistHostAsync(
        CreateSpecialistHostRequestDto request,
        CancellationToken ct = default)
        => PostLongForAsync<ModeratorDto>(
            "/api/moderators/specialist", request,
            "Host creation timed out or the writer room / voice booth is unreachable.", ct);

    public async Task ToggleModeratorAsync(int id, CancellationToken ct = default)
    {
        using var response = await Http.PostAsync($"/api/moderators/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ModeratorUsageDto?> GetModeratorUsageAsync(int id, CancellationToken ct = default)
        => await SafeGetAsync<ModeratorUsageDto>($"/api/moderators/{id}/usage", ct);

    public Task<(FireModeratorResultDto? Result, string? Error)> FireModeratorAsync(
        int id,
        CancellationToken ct = default)
        => SendForAsync<FireModeratorResultDto>(HttpMethod.Post, $"/api/moderators/{id}/fire", null, ct);

    public async Task<ModeratorDto?> SetModeratorPhotoAsync(int id, string? photoUrl, CancellationToken ct = default)
    {
        var (moderator, _) = await SendForAsync<ModeratorDto>(
            HttpMethod.Put, $"/api/moderators/{id}/photo", new ModeratorPhotoDto(photoUrl), ct);
        return moderator;
    }

    public async Task<List<PlayLogEntryDto>> GetModeratorTalksAsync(int id, CancellationToken ct = default)
        => await SafeGetAsync<List<PlayLogEntryDto>>($"/api/moderators/{id}/talks", ct) ?? [];

    public Task<(DesignedVoiceDto? Voice, string? Error)> DesignVoiceAsync(
        DesignVoiceDto request, CancellationToken ct = default)
        => PostLongForAsync<DesignedVoiceDto>(
            "/api/voices/design", request, "Voice design timed out or the studio is unreachable.", ct);

    public Task<(DesignedVoiceDto? Voice, string? Error)> RedesignVoiceAsync(
        int moderatorId, CancellationToken ct = default)
        => PostLongForAsync<DesignedVoiceDto>(
            $"/api/moderators/{moderatorId}/redesign-voice", null,
            "Voice design timed out or the studio is unreachable.", ct);

    public Task<bool> ApplyVoiceAsync(int moderatorId, ApplyVoiceDto request, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/moderators/{moderatorId}/apply-voice", request, ct);
}
