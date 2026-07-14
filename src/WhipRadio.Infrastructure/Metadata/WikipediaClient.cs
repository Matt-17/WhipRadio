using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WhipRadio.Infrastructure.Metadata;

public interface IWikipediaClient
{
    /// <summary>
    /// Fetches the plain-text page summary (free access, keyless). The summary
    /// is INPUT to paraphrasing only — it is never stored and never spoken.
    /// </summary>
    Task<string?> GetSummaryAsync(string title, string language, CancellationToken ct);
}

/// <summary>Wikipedia REST summary client; station-language wiki first, English fallback.</summary>
public sealed class WikipediaClient(
    HttpClient http,
    IOptions<MusicMetadataOptions> options,
    ILogger<WikipediaClient> logger) : IWikipediaClient
{
    public async Task<string?> GetSummaryAsync(string title, string language, CancellationToken ct)
    {
        foreach (var lang in new[] { language, "en" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var host = options.Value.WikipediaEndpointTemplate.Replace("{lang}", lang);
            var url = $"{host}/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("extract", out var extract)
                    && extract.GetString() is { Length: > 0 } summary)
                {
                    return summary;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogDebug(ex, "Wikipedia summary fetch failed for {Title} ({Lang})", title, lang);
            }
        }

        return null;
    }
}
