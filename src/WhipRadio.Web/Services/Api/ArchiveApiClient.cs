using System.Net;
using System.Net.Http.Json;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

/// <summary>Imported real music: archive listing, uploads, metadata review, and deletion.</summary>
public sealed class ArchiveApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<ArchiveApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
    public async Task<List<ArchiveTrackDto>> GetArchiveAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ArchiveTrackDto>>("/api/archive", ct) ?? [];

    public async Task<ArchiveStatusDto?> GetArchiveStatusAsync(CancellationToken ct = default)
        => await SafeGetAsync<ArchiveStatusDto>("/api/archive/status", ct);

    public Task<bool> RescanArchiveAsync(CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, "/api/archive/rescan", null, ct);

    /// <summary>Streams one audio file to the archive; large files ride the long client.</summary>
    public async Task<(ArchiveTrackDto? Track, string? Error)> UploadArchiveFileAsync(
        Stream content, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            using var streamContent = new StreamContent(content);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav");
            form.Add(streamContent, "file", fileName);

            using var response = await LongClient.PostAsync("/api/archive/upload", form, ct);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<ArchiveTrackDto>(ct), null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return (null, "Uploads are switched off in the station settings.");
            }

            return (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Upload failed — the backend is unreachable or timed out.");
        }
    }

    public async Task<List<MetadataCandidateDto>> GetArchiveCandidatesAsync(Guid trackId, CancellationToken ct = default)
        => await SafeGetAsync<List<MetadataCandidateDto>>($"/api/archive/{trackId}/candidates", ct) ?? [];

    public Task<bool> AcceptArchiveCandidateAsync(Guid trackId, Guid candidateId, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/archive/{trackId}/candidates/{candidateId}/accept", null, ct);

    public Task<bool> RejectArchiveCandidateAsync(Guid trackId, Guid candidateId, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/archive/{trackId}/candidates/{candidateId}/reject", null, ct);

    public Task<bool> KeepArchiveTrackLocalAsync(Guid trackId, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/archive/{trackId}/keep-local", null, ct);

    public async Task<int> AcceptAllMatchedArchiveTracksAsync(CancellationToken ct = default)
    {
        using var response = await Http.PostAsync("/api/archive/review/accept-matched", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        return body.TryGetProperty("promoted", out var promoted) ? promoted.GetInt32() : 0;
    }

    public Task<DeleteTrackResult> DeleteArchiveTrackAsync(Guid id, CancellationToken ct = default)
        => DeleteWithDeferAsync($"/api/archive/{id}", ct);
}
