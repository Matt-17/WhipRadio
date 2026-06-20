using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.News;

public sealed class RssNewsFeedReader(IHttpClientFactory httpClientFactory) : INewsFeedReader
{
    public const string ClientName = "news";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    public async Task<IReadOnlyList<NewsFeedEntry>> ReadAsync(
        NewsFeed feed,
        int maxItems,
        CancellationToken ct)
    {
        using var response = await httpClientFactory
            .CreateClient(ClientName)
            .GetAsync(feed.Url, ct);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var rssItems = document.Descendants("item").ToList();
        if (rssItems.Count > 0)
        {
            return rssItems
                .Select(item => ToRssEntry(item, feed.Url))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) && !string.IsNullOrWhiteSpace(entry.Url))
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        return document.Descendants(Atom + "entry")
            .Select(entry => ToAtomEntry(entry, feed.Url))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) && !string.IsNullOrWhiteSpace(entry.Url))
            .Take(Math.Max(1, maxItems))
            .ToList();
    }

    public static string ContentHash(NewsFeedEntry entry)
    {
        var input = $"{entry.Title}\n{entry.Url}\n{entry.PublishedAtUtc:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..32];
    }

    private static NewsFeedEntry ToRssEntry(XElement item, string feedUrl)
    {
        var title = Clean(item.Element("title")?.Value);
        var url = AbsoluteUrl(
            Clean(item.Element("link")?.Value)
            ?? Clean(item.Elements("guid").FirstOrDefault()?.Value),
            feedUrl);
        var summary = CleanHtml(
            item.Element("description")?.Value
            ?? item.Element(Content + "encoded")?.Value);
        var published = ParseDate(
            item.Element("pubDate")?.Value
            ?? item.Element("published")?.Value
            ?? item.Element("updated")?.Value);

        return new NewsFeedEntry(title ?? string.Empty, url ?? string.Empty, summary, published);
    }

    private static NewsFeedEntry ToAtomEntry(XElement item, string feedUrl)
    {
        var title = Clean(item.Element(Atom + "title")?.Value);
        var href = item.Elements(Atom + "link")
            .FirstOrDefault(link => string.Equals((string?)link.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("href")
            ?.Value
            ?? item.Elements(Atom + "link").FirstOrDefault()?.Attribute("href")?.Value;
        var url = AbsoluteUrl(Clean(href), feedUrl);
        var summary = CleanHtml(
            item.Element(Atom + "summary")?.Value
            ?? item.Element(Atom + "content")?.Value);
        var published = ParseDate(
            item.Element(Atom + "published")?.Value
            ?? item.Element(Atom + "updated")?.Value);

        return new NewsFeedEntry(title ?? string.Empty, url ?? string.Empty, summary, published);
    }

    private static string? AbsoluteUrl(string? url, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return Uri.TryCreate(new Uri(feedUrl), url, out var resolved) ? resolved.ToString() : url;
    }

    private static DateTime? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static string? CleanHtml(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return null;
        }

        cleaned = Regex.Replace(cleaned, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");
        return Clean(System.Net.WebUtility.HtmlDecode(cleaned));
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
