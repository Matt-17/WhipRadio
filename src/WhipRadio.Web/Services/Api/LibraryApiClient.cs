using System.Net;
using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services.Api;

public sealed record DeleteArtistResult(bool Deleted, string? Error);

/// <summary>Music library: tracks, AI artists and their members, guests, and jingles.</summary>
public sealed class LibraryApiClient(HttpClient http, IHttpClientFactory httpClientFactory, ILogger<LibraryApiClient> logger)
    : ApiClientBase(http, httpClientFactory, logger)
{
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

    public async Task<TrackDto?> GetTrackAsync(Guid id, CancellationToken ct = default)
        => await SafeGetAsync<TrackDto>($"/api/library/{id}", ct);

    public Task<DeleteTrackResult> DeleteTrackAsync(Guid id, CancellationToken ct = default)
        => DeleteWithDeferAsync($"/api/library/{id}", ct);

    public async Task<List<ArtistDto>> GetArtistsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<ArtistDto>>("/api/artists", ct) ?? [];

    public async Task<ArtistDto?> GetArtistAsync(Guid id, CancellationToken ct = default)
        => await SafeGetAsync<ArtistDto>($"/api/artists/{id}", ct);

    public async Task<PagedArtistPostsDto> GetArtistPostsAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
        => await SafeGetAsync<PagedArtistPostsDto>($"/api/artist-posts?page={page}&pageSize={pageSize}", ct)
            ?? new PagedArtistPostsDto(0, page, pageSize, []);

    public Task<(ArtistDto? Artist, string? Error)> CreateArtistAsync(string hint, CancellationToken ct = default)
        => PostLongForAsync<ArtistDto>(
            "/api/artists", new CreateArtistRequestDto(hint),
            "Artist creation timed out or the writer room is unreachable.", ct);

    public Task<(ArtistDto? Artist, string? Error)> RedefineArtistAsync(Guid id, string? hint, CancellationToken ct = default)
        => PostLongForAsync<ArtistDto>(
            $"/api/artists/{id}/redefine", new RedefineArtistRequestDto(hint),
            "Artist redefinition timed out or the writer room is unreachable.", ct);

    public Task<bool> ProduceTrackForArtistAsync(Guid id, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/artists/{id}/produce", null, ct);

    /// <summary>Queues a fresh voice reference for a band member; the play clip
    /// disappears until the booth renders the new design.</summary>
    public Task<bool> RecreateMemberVoiceAsync(Guid memberId, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/artist-members/{memberId}/voice/recreate", null, ct);

    public async Task<DeleteArtistResult> DeleteArtistAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await Http.DeleteAsync($"/api/artists/{id}", ct);
        return response.StatusCode == HttpStatusCode.NoContent
            ? new DeleteArtistResult(Deleted: true, Error: null)
            : new DeleteArtistResult(Deleted: false, Error: await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<List<GuestDto>> GetGuestsAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<GuestDto>>("/api/guests", ct) ?? [];

    public Task<(GuestDto? Guest, string? Error)> CreateGuestAsync(string? hint, CancellationToken ct = default)
        => PostLongForAsync<GuestDto>(
            "/api/guests", new CreateGuestRequestDto(hint),
            "Guest creation timed out or the writer room is unreachable.", ct);

    public Task<(GuestDto? Guest, string? Error)> RedefineGuestAsync(Guid id, string? hint, CancellationToken ct = default)
        => PostLongForAsync<GuestDto>(
            $"/api/guests/{id}/redefine", new RedefineGuestRequestDto(hint),
            "Guest redefinition timed out or the writer room is unreachable.", ct);

    public async Task<(bool Removed, string? Error)> DeleteGuestAsync(Guid id, CancellationToken ct = default)
    {
        var error = await SendReturningErrorAsync(HttpMethod.Delete, $"/api/guests/{id}", null, ct);
        return (error is null, error);
    }

    public Task<bool> RecreateGuestVoiceAsync(Guid guestId, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Post, $"/api/guests/{guestId}/voice/recreate", null, ct);

    public Task<bool> SetGuestVoiceFxAsync(Guid guestId, string? voiceFx, CancellationToken ct = default)
        => SendOkAsync(HttpMethod.Put, $"/api/guests/{guestId}/voice-fx", new UpdateGuestVoiceFxRequestDto(voiceFx), ct);

    public Task<(JingleDto? Jingle, string? Error)> CreateJingleAsync(CreateJingleDto request, CancellationToken ct = default)
        => PostLongForAsync<JingleDto>(
            "/api/jingles", request, "Jingle generation timed out or the studio is unreachable.", ct);

    public async Task<List<JingleDto>> GetJinglesAsync(CancellationToken ct = default)
        => await SafeGetAsync<List<JingleDto>>("/api/jingles", ct) ?? [];

    public async Task<JingleDto?> ToggleJingleAsync(Guid id, CancellationToken ct = default)
    {
        var (jingle, _) = await SendForAsync<JingleDto>(HttpMethod.Post, $"/api/jingles/{id}/toggle", null, ct);
        return jingle;
    }

    public Task<string?> DeleteJingleAsync(Guid id, CancellationToken ct = default)
        => SendReturningErrorAsync(HttpMethod.Delete, $"/api/jingles/{id}", null, ct);
}
