using System.Net;
using System.Text.RegularExpressions;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.News;

public sealed class HtmlArticleExtractor(IHttpClientFactory httpClientFactory) : INewsArticleExtractor
{
    private const int MaxCharacters = 6000;

    public async Task<string?> ExtractAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return null;
        }

        using var response = await httpClientFactory.CreateClient(RssNewsFeedReader.ClientName).GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        var text = ExtractReadableText(html);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string? ExtractReadableText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var body = MatchFirst(html, "<article[^>]*>(.*?)</article>")
            ?? MatchFirst(html, "<main[^>]*>(.*?)</main>")
            ?? MatchFirst(html, "<body[^>]*>(.*?)</body>")
            ?? html;

        body = Regex.Replace(body, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<noscript.*?</noscript>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<nav.*?</nav>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<footer.*?</footer>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "</p>|</h[1-6]>|</li>", "\n", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, "<[^>]+>", " ");
        body = WebUtility.HtmlDecode(body);

        var lines = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length >= 40 && !LooksLikeBoilerplate(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20);
        var text = string.Join("\n", lines);
        return text.Length <= MaxCharacters ? text : text[..MaxCharacters];
    }

    private static string? MatchFirst(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool LooksLikeBoilerplate(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("subscribe")
            || lower.Contains("sign up")
            || lower.Contains("privacy policy")
            || lower.Contains("cookie")
            || lower.Contains("advertisement");
    }
}
