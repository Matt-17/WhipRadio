using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Metadata;

namespace WhipRadio.Infrastructure.Metadata;

public interface IMusicBrainzClient
{
    /// <summary>Searches recordings by local evidence; returns scored-input candidates.</summary>
    Task<IReadOnlyList<RecordingCandidate>> SearchRecordingsAsync(TrackMatchEvidence evidence, CancellationToken ct);

    /// <summary>Resolves an artist's Wikidata QID via its URL relations, if any.</summary>
    Task<string?> GetArtistWikidataQidAsync(string artistMbid, CancellationToken ct);
}

/// <summary>
/// Keyless MusicBrainz web-service client (CC0 core data). All calls pass the
/// process-wide <see cref="MusicBrainzRateGate"/>; a 503 with Retry-After gets
/// exactly one courteous retry. Failures bubble up — the enrichment service is
/// failure-soft and leaves the track LocalOnly.
/// </summary>
public sealed class MusicBrainzClient(
    HttpClient http,
    MusicBrainzRateGate rateGate,
    ILogger<MusicBrainzClient> logger) : IMusicBrainzClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RecordingCandidate>> SearchRecordingsAsync(
        TrackMatchEvidence evidence, CancellationToken ct)
    {
        var query = BuildRecordingQuery(evidence);
        if (query is null)
        {
            return [];
        }

        var url = $"/ws/2/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=8";
        using var document = await GetJsonAsync(url, ct);
        if (!document.RootElement.TryGetProperty("recordings", out var recordings))
        {
            return [];
        }

        var candidates = new List<RecordingCandidate>();
        foreach (var recording in recordings.EnumerateArray())
        {
            var id = recording.GetProperty("id").GetString();
            var title = recording.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (id is null || title is null)
            {
                continue;
            }

            string? artistName = null;
            string? artistId = null;
            if (recording.TryGetProperty("artist-credit", out var credits)
                && credits.ValueKind == JsonValueKind.Array
                && credits.GetArrayLength() > 0)
            {
                var artist = credits[0].GetProperty("artist");
                artistName = artist.TryGetProperty("name", out var n) ? n.GetString() : null;
                artistId = artist.TryGetProperty("id", out var aid) ? aid.GetString() : null;
            }

            string? album = null;
            int? year = null;
            int? trackNumber = null;
            if (recording.TryGetProperty("releases", out var releases)
                && releases.ValueKind == JsonValueKind.Array
                && releases.GetArrayLength() > 0)
            {
                var release = releases[0];
                album = release.TryGetProperty("title", out var rt) ? rt.GetString() : null;
                if (release.TryGetProperty("date", out var date)
                    && date.GetString() is { Length: >= 4 } dateText
                    && int.TryParse(dateText[..4], out var parsedYear))
                {
                    year = parsedYear;
                }

                if (release.TryGetProperty("media", out var media)
                    && media.ValueKind == JsonValueKind.Array
                    && media.GetArrayLength() > 0
                    && media[0].TryGetProperty("track", out var tracks)
                    && tracks.ValueKind == JsonValueKind.Array
                    && tracks.GetArrayLength() > 0
                    && tracks[0].TryGetProperty("number", out var number)
                    && int.TryParse(number.GetString(), out var parsedNumber))
                {
                    trackNumber = parsedNumber;
                }
            }

            double? duration = recording.TryGetProperty("length", out var length)
                && length.ValueKind == JsonValueKind.Number
                    ? length.GetInt64() / 1000.0
                    : null;

            var isrcs = new List<string>();
            if (recording.TryGetProperty("isrcs", out var isrcArray) && isrcArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var isrc in isrcArray.EnumerateArray())
                {
                    // Search results carry ISRCs either as plain strings or as {id} objects.
                    var value = isrc.ValueKind switch
                    {
                        JsonValueKind.String => isrc.GetString(),
                        JsonValueKind.Object when isrc.TryGetProperty("id", out var isrcId) => isrcId.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        isrcs.Add(value);
                    }
                }
            }

            candidates.Add(new RecordingCandidate(
                id, title, artistName ?? string.Empty, artistId, album, year, trackNumber, duration, isrcs));
        }

        return candidates;
    }

    public async Task<string?> GetArtistWikidataQidAsync(string artistMbid, CancellationToken ct)
    {
        using var document = await GetJsonAsync($"/ws/2/artist/{artistMbid}?inc=url-rels&fmt=json", ct);
        if (!document.RootElement.TryGetProperty("relations", out var relations))
        {
            return null;
        }

        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "wikidata", StringComparison.OrdinalIgnoreCase)
                && relation.TryGetProperty("url", out var urlNode)
                && urlNode.TryGetProperty("resource", out var resource)
                && resource.GetString() is { } url)
            {
                var qid = url.TrimEnd('/').Split('/')[^1];
                return qid.StartsWith('Q') ? qid : null;
            }
        }

        return null;
    }

    /// <summary>Lucene query from the strongest available local evidence.</summary>
    internal static string? BuildRecordingQuery(TrackMatchEvidence evidence)
    {
        if (!string.IsNullOrWhiteSpace(evidence.Isrc))
        {
            return $"isrc:{Escape(evidence.Isrc)}";
        }

        if (string.IsNullOrWhiteSpace(evidence.Title))
        {
            return null;
        }

        var builder = new StringBuilder($"recording:\"{Escape(evidence.Title)}\"");
        if (!string.IsNullOrWhiteSpace(evidence.Artist))
        {
            builder.Append($" AND artist:\"{Escape(evidence.Artist)}\"");
        }

        if (!string.IsNullOrWhiteSpace(evidence.Album))
        {
            builder.Append($" AND release:\"{Escape(evidence.Album)}\"");
        }

        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await rateGate.WaitAsync(ct);
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0)
            {
                // One courteous retry honoring Retry-After; the rate gate stays in charge otherwise.
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                logger.LogDebug("MusicBrainz 503 — retrying once after {Delay}s", delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }
}
