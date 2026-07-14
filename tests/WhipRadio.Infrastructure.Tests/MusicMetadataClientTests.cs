using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Metadata;
using WhipRadio.Infrastructure.Metadata;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class MusicMetadataClientTests
{
    private const string RecordingSearchJson = """
{
  "recordings": [
    {
      "id": "rec-1",
      "title": "Teardrop",
      "length": 330000,
      "artist-credit": [ { "artist": { "id": "art-1", "name": "Massive Attack" } } ],
      "releases": [
        {
          "title": "Mezzanine",
          "date": "1998-04-20",
          "media": [ { "track": [ { "number": "3" } ] } ]
        }
      ],
      "isrcs": [ "GBAAA9800001" ]
    }
  ]
}
""";

    private const string ArtistRelationsJson = """
{
  "relations": [
    { "type": "official homepage", "url": { "resource": "https://massiveattack.co.uk" } },
    { "type": "wikidata", "url": { "resource": "https://www.wikidata.org/wiki/Q153048" } }
  ]
}
""";

    private const string WikidataEntityJson = """
{
  "entities": {
    "Q153048": {
      "labels": { "en": { "value": "Massive Attack" } },
      "descriptions": { "en": { "value": "British trip hop group" } },
      "claims": {
        "P571": [ { "mainsnak": { "datavalue": { "value": { "time": "+1988-00-00T00:00:00Z" } } } } ],
        "P136": [ { "mainsnak": { "datavalue": { "value": { "id": "Q205560" } } } } ],
        "P740": [ { "mainsnak": { "datavalue": { "value": { "id": "Q23154" } } } } ]
      },
      "sitelinks": { "enwiki": { "title": "Massive Attack" } }
    }
  }
}
""";

    [TestMethod]
    public async Task MusicBrainz_ParsesRecordingSearchResults()
    {
        var client = MusicBrainz(new FakeHandler(_ => Json(RecordingSearchJson)));

        var candidates = await client.SearchRecordingsAsync(
            new TrackMatchEvidence("Teardrop", "Massive Attack"), CancellationToken.None);

        Assert.Equal(1, candidates.Count);
        var candidate = candidates[0];
        Assert.Equal("rec-1", candidate.RecordingId);
        Assert.Equal("Teardrop", candidate.Title);
        Assert.Equal("Massive Attack", candidate.Artist);
        Assert.Equal("art-1", candidate.ArtistId);
        Assert.Equal("Mezzanine", candidate.Album);
        Assert.Equal(1998, candidate.Year);
        Assert.Equal(3, candidate.TrackNumber);
        Assert.Equal(330.0, candidate.DurationSeconds!.Value, 3);
        Assert.Contains("GBAAA9800001", candidate.Isrcs!);
    }

    [TestMethod]
    public async Task MusicBrainz_ResolvesWikidataQidFromUrlRelations()
    {
        var client = MusicBrainz(new FakeHandler(_ => Json(ArtistRelationsJson)));

        var qid = await client.GetArtistWikidataQidAsync("art-1", CancellationToken.None);

        Assert.Equal("Q153048", qid);
    }

    [TestMethod]
    public async Task MusicBrainz_RetriesOnceOn503()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10)) },
                }
                : Json(ArtistRelationsJson);
        });

        var qid = await MusicBrainz(handler).GetArtistWikidataQidAsync("art-1", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("Q153048", qid);
    }

    [TestMethod]
    public void MusicBrainz_QueryPrefersIsrcThenQuotedFields()
    {
        Assert.Equal(
            "isrc:GBAAA9800001",
            MusicBrainzClient.BuildRecordingQuery(new TrackMatchEvidence("T", "A", Isrc: "GBAAA9800001")));
        Assert.Equal(
            "recording:\"Teardrop\" AND artist:\"Massive Attack\" AND release:\"Mezzanine\"",
            MusicBrainzClient.BuildRecordingQuery(new TrackMatchEvidence("Teardrop", "Massive Attack", "Mezzanine")));
        Assert.Null(MusicBrainzClient.BuildRecordingQuery(new TrackMatchEvidence(null, "Artist Only")));
    }

    [TestMethod]
    public async Task RateGate_SpacesRequestStarts()
    {
        var gate = new MusicBrainzRateGate(
            Options.Create(new MusicMetadataOptions { MusicBrainzMinRequestIntervalMs = 200 }),
            TimeProvider.System);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 380,
            $"three request starts took only {stopwatch.ElapsedMilliseconds} ms — gate not spacing");
    }

    [TestMethod]
    public async Task Wikidata_ExtractsStructuredFactsAndSitelink()
    {
        var client = new WikidataClient(
            new HttpClient(new FakeHandler(_ => Json(WikidataEntityJson))) { BaseAddress = new Uri("https://wikidata.test") },
            NullLogger<WikidataClient>.Instance);

        var facts = await client.GetArtistFactsAsync("Q153048", "en", CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Equal("Massive Attack", facts!.Name);
        Assert.Equal("British trip hop group", facts.Description);
        Assert.Equal(1988, facts.FormedYear);
        Assert.Null(facts.DissolvedYear);
        Assert.Equal("Q23154", facts.OriginLabelQid);
        Assert.Contains("Q205560", facts.GenreQids);
        Assert.Equal("Massive Attack", facts.WikipediaTitle);
        Assert.Equal("en", facts.WikipediaLanguage);
    }

    [TestMethod]
    public async Task Wikipedia_ReadsTheSummaryExtract()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Contains("en.wikipedia.test", request.RequestUri!.Host);
            return Json("""{ "extract": "Massive Attack are a British music group." }""");
        });
        var client = new WikipediaClient(
            new HttpClient(handler),
            Options.Create(new MusicMetadataOptions { WikipediaEndpointTemplate = "https://{lang}.wikipedia.test" }),
            NullLogger<WikipediaClient>.Instance);

        var summary = await client.GetSummaryAsync("Massive Attack", "en", CancellationToken.None);

        Assert.Equal("Massive Attack are a British music group.", summary);
    }

    [TestMethod]
    public async Task DigestWriter_ParsesTheStructuredReplyIntoOneDigestString()
    {
        var llm = new StaticLlm("""{ "facts": ["Formed in Bristol in 1988.", "Pioneers of trip hop."] }""");
        var writer = new KnowledgeDigestWriter(llm, NullLogger<KnowledgeDigestWriter>.Instance);

        var digest = await writer.WriteAsync(
            "Massive Attack",
            new Dictionary<string, string> { ["Formed"] = "1988" },
            "Some source summary that must not be stored.",
            CancellationToken.None);

        Assert.Equal("Formed in Bristol in 1988. Pioneers of trip hop.", digest);
    }

    [TestMethod]
    public async Task DigestWriter_FailsSoftOnGarbageReply()
    {
        var writer = new KnowledgeDigestWriter(new StaticLlm("not json"), NullLogger<KnowledgeDigestWriter>.Instance);

        var digest = await writer.WriteAsync(
            "Massive Attack", new Dictionary<string, string> { ["Formed"] = "1988" }, null, CancellationToken.None);

        Assert.Null(digest);
    }

    private static MusicBrainzClient MusicBrainz(FakeHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.test") },
            new MusicBrainzRateGate(
                Options.Create(new MusicMetadataOptions { MusicBrainzMinRequestIntervalMs = 0 }),
                TimeProvider.System),
            NullLogger<MusicBrainzClient>.Instance);

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StaticLlm(string reply) : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(reply);

        public Task<string> CompleteAsync(TextGenerationRequest request, CancellationToken ct)
            => Task.FromResult(reply);
    }
}
