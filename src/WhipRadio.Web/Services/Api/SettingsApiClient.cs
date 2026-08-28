using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Station configuration: settings, branding, mixer, formats, schedule, privacy.</summary>
public sealed class SettingsApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<SettingsApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<StationSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
        => await SafeGetAsync<StationSettingsDto>("/api/settings", ct);

    public async Task<StationSettingsDto?> SaveSettingsAsync(StationSettingsDto settings, CancellationToken ct = default)
    {
        using var response = await Http.PutAsJsonAsync("/api/settings", settings, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StationSettingsDto>(ct);
    }

    public async Task<BrandingDto?> GetBrandingAsync(CancellationToken ct = default)
        => await SafeGetAsync<BrandingDto>("/api/branding", ct);

    public async Task<BrandingDto?> SaveBrandingAsync(SaveBrandingDto branding, CancellationToken ct = default)
    {
        using var response = await Http.PutAsJsonAsync("/api/branding", branding, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingDto>(ct);
    }

    public async Task<MixerOverviewDto?> GetMixerAsync(CancellationToken ct = default)
        => await SafeGetAsync<MixerOverviewDto>("/api/mixer", ct);

    public Task<string?> SaveMixerSettingsAsync(MixerSettingsDto settings, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Put, "/api/mixer/settings", settings, ct);

    public async Task RunMixerBackfillAsync(CancellationToken ct = default)
        => await Http.PostAsync("/api/mixer/backfill", null, ct);

    public async Task<List<FormatDto>> GetFormatsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<FormatDto>>("/api/formats", ct) ?? [];

    public async Task ToggleFormatAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await Http.PostAsync($"/api/formats/{id}/toggle", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task VoteFormatAsync(Guid id, int direction, CancellationToken ct = default)
    {
        using var response = await Http.PostAsync($"/api/formats/{id}/vote?direction={direction}", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<ProgramSlotDto>> GetScheduleAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ProgramSlotDto>>("/api/schedule", ct) ?? [];

    public async Task<PrivacyReportDto?> GetPrivacyAsync(CancellationToken ct = default)
        => await SafeGetAsync<PrivacyReportDto>("/api/privacy", ct);
}
