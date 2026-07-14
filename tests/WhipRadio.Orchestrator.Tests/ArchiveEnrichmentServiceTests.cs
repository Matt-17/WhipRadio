using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Core.Metadata;
using WhipRadio.Infrastructure.Metadata;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ArchiveEnrichmentServiceTests
{
    [TestMethod]
    public async Task RunCycle_StrongAnchor_AutoMatchesAppliesClaimsAndGathersKnowledge()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var file = Path.Combine(dataRoot, "teardrop.wav");
            Directory.CreateDirectory(dataRoot);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var trackId = await SeedTrackAsync(fixture, file, title: "teardrop (rip)", artist: "massive attack");

            var musicBrainz = new FakeMusicBrainz(
                [new RecordingCandidate("rec-1", "Teardrop", "Massive Attack", "art-1", "Mezzanine", 1998, 3, 330)],
                qid: "Q153048");
            var service = CreateService(
                fixture, dataRoot, musicBrainz,
                tags: new FileTags(MusicBrainzRecordingId: "rec-1", DurationSeconds: 330));

            await service.RunCycleAsync("en", CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Equal(MetadataStatus.AutoMatched, track.MetadataStatus);
            Assert.Equal("Teardrop", track.Title);
            Assert.Equal("Massive Attack", track.ImportedArtist);
            Assert.Equal("Mezzanine", track.ImportedAlbum);
            Assert.Equal(1998, track.ImportedYear);
            Assert.NotNull(track.LastEnrichmentAttemptUtc);

            // Field-level provenance: original tags preserved, MusicBrainz values applied.
            var claims = await db.MetadataClaims.AsNoTracking().Where(c => c.OwnerId == trackId).ToListAsync();
            Assert.Contains(claims, c => c is { Source: "FileTags", FieldName: "Title", Value: "teardrop (rip)", IsApplied: false });
            Assert.Contains(claims, c => c is { Source: "MusicBrainz", FieldName: "Title", Value: "Teardrop", IsApplied: true });

            var externalIds = await db.ExternalIds.AsNoTracking().Where(e => e.OwnerId == trackId).ToListAsync();
            Assert.Contains(externalIds, e => e is { Source: "MusicBrainz", EntityType: "Recording", Value: "rec-1" });
            Assert.Contains(externalIds, e => e is { Source: "Wikidata", EntityType: "Qid", Value: "Q153048" });

            var knowledge = await db.KnowledgeEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Massive Attack", knowledge.DisplayName);
            Assert.Equal("Q153048", knowledge.SourceEntityId);
            Assert.Contains("Bristol", knowledge.Digest);
            // The Wikipedia summary itself is never stored.
            Assert.DoesNotContain("SOURCE-SUMMARY-MARKER", knowledge.Digest);
            Assert.DoesNotContain("SOURCE-SUMMARY-MARKER", knowledge.FactsJson);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task RunCycle_AmbiguousCandidates_AreStoredWithoutTouchingDisplayFields()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var trackId = await SeedTrackAsync(
                fixture, Path.Combine(dataRoot, "missing.wav"), title: "Teardrop Live Bootleg 1997", artist: "M. Attack");

            var musicBrainz = new FakeMusicBrainz(
            [
                new RecordingCandidate("rec-1", "Teardrop", "Massive Attack", "art-1", null, 1998, null, null),
                new RecordingCandidate("rec-2", "Teardrop (live)", "Massive Attack", "art-1", null, 1998, null, null),
            ]);
            var service = CreateService(fixture, dataRoot, musicBrainz);

            await service.RunCycleAsync("en", CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Equal(MetadataStatus.Ambiguous, track.MetadataStatus);
            Assert.Equal("Teardrop Live Bootleg 1997", track.Title);
            Assert.Equal("M. Attack", track.ImportedArtist);

            var candidates = await db.MetadataCandidates.AsNoTracking().Where(c => c.TrackId == trackId).ToListAsync();
            Assert.Equal(2, candidates.Count);
            Assert.All(candidates, c => Assert.Equal(CandidateStatus.Pending, c.Status));
            Assert.Equal(0, await db.KnowledgeEntries.CountAsync());
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task RunCycle_NoCandidates_MarksNeedsReviewAndStampsTheAttempt()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var trackId = await SeedTrackAsync(
                fixture, Path.Combine(dataRoot, "missing.wav"), title: "track07_final", artist: null);
            var service = CreateService(fixture, dataRoot, new FakeMusicBrainz([]));

            await service.RunCycleAsync("en", CancellationToken.None);
            // A second cycle must skip the track (cool-down).
            await service.RunCycleAsync("en", CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Equal(MetadataStatus.NeedsReview, track.MetadataStatus);
            Assert.NotNull(track.LastEnrichmentAttemptUtc);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    // --- fixture plumbing -----------------------------------------------------

    private static async Task<Guid> SeedTrackAsync(DbFixture fixture, string filePath, string title, string? artist)
    {
        await using var db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
        }

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Source = TrackSource.External,
            Backend = "library",
            Title = title,
            ImportedArtist = artist,
            FilePath = filePath,
            MetadataStatus = MetadataStatus.LocalOnly,
            DurationSeconds = 330,
            CreatedAt = DateTime.UtcNow,
        };
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track.Id;
    }

    private static ArchiveEnrichmentService CreateService(
        DbFixture fixture, string dataRoot, FakeMusicBrainz musicBrainz, FileTags? tags = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMusicBrainzClient>(musicBrainz);
        services.AddSingleton(new WikidataClient(
            new HttpClient(new FakeHandler(WikidataEntityJson)) { BaseAddress = new Uri("https://wikidata.test") },
            NullLogger<WikidataClient>.Instance));
        services.AddSingleton<IWikipediaClient>(new FakeWikipedia());
        services.AddSingleton(new KnowledgeDigestWriter(
            new StaticLlm("""{ "facts": ["Formed in Bristol in 1988."] }"""),
            NullLogger<KnowledgeDigestWriter>.Instance));
        var provider = services.BuildServiceProvider();

        return new ArchiveEnrichmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture,
            new WhipRadio.Infrastructure.Persistence.StationSettingsCache(fixture, TimeProvider.System),
            new StubTagReader(tags ?? new FileTags()),
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            Options.Create(new MusicMetadataOptions()),
            new NoOpPublisher(),
            TimeProvider.System,
            NullLogger<ArchiveEnrichmentService>.Instance);
    }

    private const string WikidataEntityJson = """
{
  "entities": {
    "Q153048": {
      "labels": { "en": { "value": "Massive Attack" } },
      "descriptions": { "en": { "value": "British trip hop group" } },
      "claims": {
        "P571": [ { "mainsnak": { "datavalue": { "value": { "time": "+1988-00-00T00:00:00Z" } } } } ]
      },
      "sitelinks": { "enwiki": { "title": "Massive Attack" } }
    }
  }
}
""";

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "whipradio-enrichment-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeMusicBrainz(
        IReadOnlyList<RecordingCandidate> candidates, string? qid = null) : IMusicBrainzClient
    {
        public Task<IReadOnlyList<RecordingCandidate>> SearchRecordingsAsync(
            TrackMatchEvidence evidence, CancellationToken ct)
            => Task.FromResult(candidates);

        public Task<string?> GetArtistWikidataQidAsync(string artistMbid, CancellationToken ct)
            => Task.FromResult(qid);
    }

    private sealed class FakeWikipedia : IWikipediaClient
    {
        public Task<string?> GetSummaryAsync(string title, string language, CancellationToken ct)
            => Task.FromResult<string?>("SOURCE-SUMMARY-MARKER: Massive Attack are a British group.");
    }

    private sealed class StubTagReader(FileTags tags) : IFileTagReader
    {
        public FileTags Read(string absolutePath) => tags;
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StaticLlm(string reply) : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(reply);

        public Task<string> CompleteAsync(TextGenerationRequest request, CancellationToken ct)
            => Task.FromResult(reply);
    }

    private sealed class NoOpPublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishConversationsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishArchiveChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
