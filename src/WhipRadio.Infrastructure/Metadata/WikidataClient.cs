using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WhipRadio.Infrastructure.Metadata;

/// <summary>Structured facts about an artist entity — never article prose (Phase 6a §3.2).</summary>
public sealed record ArtistFacts(
    string Qid,
    string? Name,
    string? Description,
    int? FormedYear,
    int? DissolvedYear,
    string? OriginLabelQid,
    IReadOnlyList<string> GenreQids,
    IReadOnlyList<string> MemberQids,
    string? WikipediaTitle,
    string? WikipediaLanguage);

public interface IWikidataClient
{
    Task<ArtistFacts?> GetArtistFactsAsync(string qid, string preferredLanguage, CancellationToken ct);
}

/// <summary>
/// Keyless Wikidata entity-data client (CC0). Reads only structured claims:
/// inception (P571), dissolution (P576), origin (P740/P495), genres (P136),
/// members (P527), and the sitelink to a Wikipedia article title in the
/// station language (falling back to English).
/// </summary>
public sealed class WikidataClient(HttpClient http, ILogger<WikidataClient> logger) : IWikidataClient
{
    public async Task<ArtistFacts?> GetArtistFactsAsync(string qid, string preferredLanguage, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/wiki/Special:EntityData/{Uri.EscapeDataString(qid)}.json", ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Wikidata entity {Qid} fetch returned {Status}", qid, response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(qid, out var entity))
        {
            return null;
        }

        var name = Label(entity, "labels", preferredLanguage) ?? Label(entity, "labels", "en");
        var description = Label(entity, "descriptions", preferredLanguage) ?? Label(entity, "descriptions", "en");

        var claims = entity.TryGetProperty("claims", out var c) ? c : default;
        var (wikiTitle, wikiLanguage) = Sitelink(entity, preferredLanguage);

        return new ArtistFacts(
            qid,
            name,
            description,
            FormedYear: TimeYear(claims, "P571"),
            DissolvedYear: TimeYear(claims, "P576"),
            OriginLabelQid: ItemId(claims, "P740") ?? ItemId(claims, "P495"),
            GenreQids: ItemIds(claims, "P136"),
            MemberQids: ItemIds(claims, "P527"),
            WikipediaTitle: wikiTitle,
            WikipediaLanguage: wikiLanguage);
    }

    /// <summary>Resolves entity labels (e.g. genre/origin QIDs) to display names.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetLabelsAsync(
        IReadOnlyCollection<string> qids, string preferredLanguage, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var qid in qids.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            try
            {
                using var response = await http.GetAsync(
                    $"/wiki/Special:EntityData/{Uri.EscapeDataString(qid)}.json", ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("entities", out var entities)
                    && entities.TryGetProperty(qid, out var entity)
                    && (Label(entity, "labels", preferredLanguage) ?? Label(entity, "labels", "en")) is { } label)
                {
                    labels[qid] = label;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogDebug(ex, "Wikidata label fetch failed for {Qid}", qid);
            }
        }

        return labels;
    }

    private static string? Label(JsonElement entity, string section, string language)
        => entity.TryGetProperty(section, out var labels)
            && labels.TryGetProperty(language, out var label)
            && label.TryGetProperty("value", out var value)
                ? value.GetString()
                : null;

    private static (string? Title, string? Language) Sitelink(JsonElement entity, string preferredLanguage)
    {
        if (!entity.TryGetProperty("sitelinks", out var sitelinks))
        {
            return (null, null);
        }

        foreach (var language in new[] { preferredLanguage, "en" })
        {
            if (sitelinks.TryGetProperty($"{language}wiki", out var link)
                && link.TryGetProperty("title", out var title))
            {
                return (title.GetString(), language);
            }
        }

        return (null, null);
    }

    private static int? TimeYear(JsonElement claims, string property)
    {
        if (claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty(property, out var statements))
        {
            return null;
        }

        foreach (var statement in statements.EnumerateArray())
        {
            if (statement.TryGetProperty("mainsnak", out var snak)
                && snak.TryGetProperty("datavalue", out var value)
                && value.TryGetProperty("value", out var time)
                && time.TryGetProperty("time", out var timeText)
                && timeText.GetString() is { Length: >= 5 } text
                && int.TryParse(text[1..5], out var year))
            {
                return year;
            }
        }

        return null;
    }

    private static string? ItemId(JsonElement claims, string property)
        => ItemIds(claims, property).FirstOrDefault();

    private static IReadOnlyList<string> ItemIds(JsonElement claims, string property)
    {
        if (claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty(property, out var statements))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var statement in statements.EnumerateArray())
        {
            if (statement.TryGetProperty("mainsnak", out var snak)
                && snak.TryGetProperty("datavalue", out var value)
                && value.TryGetProperty("value", out var item)
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id)
                && id.GetString() is { } qid)
            {
                ids.Add(qid);
            }
        }

        return ids;
    }
}
