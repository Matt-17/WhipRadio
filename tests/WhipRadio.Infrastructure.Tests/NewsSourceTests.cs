using System.Net;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.News;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class NewsSourceTests
{
    [TestMethod]
    public async Task RssNewsFeedReader_ReadAsync_ParsesRssItems()
    {
        const string rss = """
            <rss version="2.0">
              <channel>
                <item>
                  <title>Major chip startup launches new accelerator</title>
                  <link>https://example.test/story</link>
                  <description><![CDATA[<p>The company says the accelerator targets AI inference.</p>]]></description>
                  <pubDate>Fri, 19 Jun 2026 12:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            new StringContent(rss, System.Text.Encoding.UTF8, "application/rss+xml"));
        var reader = new RssNewsFeedReader(new SingleClientFactory(handler.CreateClient()));

        var entries = await reader.ReadAsync(new NewsFeed
        {
            Label = "Example",
            Url = "https://example.test/rss.xml",
        }, 10, CancellationToken.None);

        Assert.Equal(1, entries.Count);
        Assert.Equal("Major chip startup launches new accelerator", entries[0].Title);
        Assert.Equal("https://example.test/story", entries[0].Url);
        Assert.Equal("The company says the accelerator targets AI inference.", entries[0].Summary);
        Assert.Equal(new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc), entries[0].PublishedAtUtc);
    }

    [TestMethod]
    public void HtmlArticleExtractor_ExtractReadableText_RemovesBoilerplateAndMarkup()
    {
        const string html = """
            <html>
              <body>
                <nav>Subscribe now and sign up</nav>
                <article>
                  <h1>Headline</h1>
                  <p>The first paragraph contains the core facts and enough words to be useful for a rewritten radio news summary.</p>
                  <p>Cookie policy and privacy policy links should not dominate the extracted article text.</p>
                  <p>The second paragraph adds context about the timing, companies involved, and why listeners should care.</p>
                </article>
              </body>
            </html>
            """;

        var text = HtmlArticleExtractor.ExtractReadableText(html);

        Assert.NotNull(text);
        Assert.Contains("The first paragraph contains the core facts", text);
        Assert.Contains("The second paragraph adds context", text);
        Assert.DoesNotContain("Subscribe now", text);
        Assert.DoesNotContain("Cookie policy", text);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
