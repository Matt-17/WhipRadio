using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Studio endpoints: overview, history, connection test, and CRUD.</summary>
public sealed class StudiosApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<StudiosApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<StudioOverviewDto> GetStudioOverviewAsync(CancellationToken ct = default)
        => await SafeGetAsync<StudioOverviewDto>("/api/studios", ct) ?? new StudioOverviewDto([], []);

    public async Task<List<StudioDto>> GetStudiosAsync(CancellationToken ct = default)
        => (await GetStudioOverviewAsync(ct)).Studios.ToList();

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
        using var response = await Http.GetAsync(url, ct);
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
            using var response = await Http.PostAsJsonAsync("/api/studios/test", request, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<StudioTestResultDto>(ct)
                : new StudioTestResultDto(false, null, $"Test endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new StudioTestResultDto(false, null, "Orchestrator not reachable.");
        }
    }

    public Task<(StudioDto? Studio, string? Error)> CreateStudioAsync(SaveStudioDto request, CancellationToken ct = default)
        => SendForAsync<StudioDto>(HttpMethod.Post, "/api/studios", request, ct);

    public Task<(StudioDto? Studio, string? Error)> UpdateStudioAsync(Guid id, SaveStudioDto request, CancellationToken ct = default)
        => SendForAsync<StudioDto>(HttpMethod.Put, $"/api/studios/{id}", request, ct);

    public async Task ToggleStudioAsync(Guid id, CancellationToken ct = default)
        => await Http.PostAsync($"/api/studios/{id}/toggle", null, ct);

    public async Task<StudioRestartResultDto> RestartStudioAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.PostAsync($"/api/studios/{id}/restart", null, ct);
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

    public Task<string?> DeleteStudioAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Delete, $"/api/studios/{id}", null, ct);
}
