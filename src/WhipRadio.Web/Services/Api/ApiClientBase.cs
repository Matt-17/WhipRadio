using System.Net;
using System.Net.Http.Json;

namespace WhipRadio.Web.Services.Api;

public sealed record DeleteTrackResult(bool Deleted, bool Deferred, string? Error);

/// <summary>
/// Shared conventions for the typed orchestrator API clients: GETs degrade to
/// null/empty while the orchestrator is starting, mutations return
/// <c>(Dto?, string?)</c> tuples with the raw error body, and minutes-long calls
/// ride the "orchestrator-long" client (no retry pipeline, 25 min timeout).
/// </summary>
public abstract class ApiClientBase(HttpClient http, IHttpClientFactory httpClientFactory, ILogger logger)
{
    protected HttpClient Http => http;

    protected ILogger Logger => logger;

    /// <summary>Minutes-long calls (voice design, artist creation, news production).</summary>
    protected HttpClient LongClient => httpClientFactory.CreateClient("orchestrator-long");

    protected async Task<T?> SafeGetAsync<T>(string url, CancellationToken ct) where T : class
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

    /// <summary>Mutation returning the DTO on success or the raw error body.</summary>
    protected async Task<(T? Value, string? Error)> SendForAsync<T>(
        HttpMethod method, string url, object? body, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(http, method, url, body, ct);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync<T>(ct), null)
            : (null, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Long-running POST with a friendly timeout/unreachable degradation.</summary>
    protected async Task<(T? Value, string? Error)> PostLongForAsync<T>(
        string url, object? body, string unreachableMessage, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await SendAsync(LongClient, HttpMethod.Post, url, body, ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<T>(ct), null)
                : (null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, unreachableMessage);
        }
    }

    /// <summary>Mutation returning null on success or the raw error body.</summary>
    protected async Task<string?> SendReturningErrorAsync(
        HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(http, method, url, body, ct);
        return response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Mutation where only success/failure matters.</summary>
    protected async Task<bool> SendOkAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(http, method, url, body, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Delete that the backend may defer (item still on air): 204 = deleted,
    /// 202 = deferred, anything else carries the error body.</summary>
    protected async Task<DeleteTrackResult> DeleteWithDeferAsync(string url, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(url, ct);
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

    protected static string SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No response body.";
        }

        var oneLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 320 ? oneLine : $"{oneLine[..317]}...";
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        if (method == HttpMethod.Post)
        {
            return body is null ? client.PostAsync(url, null, ct) : client.PostAsJsonAsync(url, body, ct);
        }

        if (method == HttpMethod.Put)
        {
            return client.PutAsJsonAsync(url, body!, ct);
        }

        if (method == HttpMethod.Delete)
        {
            return client.DeleteAsync(url, ct);
        }

        throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported API mutation method.");
    }
}

/// <summary>Same-origin media proxy URLs — browser-safe regardless of scheme/host.</summary>
public static class MediaUrls
{
    public static string Track(Guid id) => $"/media/track/{id}";

    public static string ArtistMemberVoice(Guid id) => $"/media/artist-member-voice/{id}";

    public static string GuestVoice(Guid id) => $"/media/guest-voice/{id}";

    public static string Announcement(Guid id) => $"/media/announcement/{id}";

    public static string Jingle(Guid id) => $"/media/jingle/{id}";

    public static string VoicePreview(string handle) => $"/media/voice-preview/{Uri.EscapeDataString(handle)}";
}
