using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class KnowledgeContextTests
{
    [TestMethod]
    public async Task Resolver_GatesDigestsByMetadataStatusAndSetting()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var verifiedTrack = await SeedKnowledgeAsync(fixture, MetadataStatus.Verified, "Q1", "Massive Attack",
            "Formed in Bristol in 1988.");
        var matchedTrack = await SeedKnowledgeAsync(fixture, MetadataStatus.Matched, "Q2", "Portishead",
            "Formed in Bristol in 1991.");
        var ambiguousTrack = await SeedKnowledgeAsync(fixture, MetadataStatus.Ambiguous, "Q3", "Tricky",
            "Started as a Massive Attack collaborator.");

        var resolver = CreateResolver(fixture);
        var facts = await resolver.ResolveForSegmentAsync(SegmentReferencing(verifiedTrack, matchedTrack, ambiguousTrack), CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Contains("Massive Attack: Formed in Bristol in 1988.", facts!);
        Assert.Contains("Portishead", facts);
        Assert.Contains("keep factual claims light", facts); // Matched → cautious
        Assert.DoesNotContain("Tricky", facts);              // Ambiguous → nothing

        // Toggle off → no knowledge at all.
        await using (var db = fixture.CreateDbContext())
        {
            (await db.StationSettings.SingleAsync()).PodcastKnowledgeEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.Null(await CreateResolver(fixture).ResolveForSegmentAsync(
            SegmentReferencing(verifiedTrack), CancellationToken.None));
    }

    [TestMethod]
    public async Task Resolver_FindsArtistsNamedInTheTopicWithoutReferencedTracks()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await SeedKnowledgeAsync(fixture, MetadataStatus.Verified, "Q1", "Massive Attack", "Formed in Bristol in 1988.");

        var segment = new ConversationSegment
        {
            Id = Guid.NewGuid(),
            Topic = "The legacy of Massive Attack",
            Brief = "Dig into the Bristol sound.",
            ReferencedTrackIdsJson = "[]",
        };

        var facts = await CreateResolver(fixture).ResolveForSegmentAsync(segment, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Contains("Massive Attack: Formed in Bristol in 1988.", facts!);
    }

    private static KnowledgeContextResolver CreateResolver(DbFixture fixture)
        => new(
            fixture,
            new StationSettingsCache(fixture, TimeProvider.System),
            NullLogger<KnowledgeContextResolver>.Instance);

    private static ConversationSegment SegmentReferencing(params Guid[] trackIds) => new()
    {
        Id = Guid.NewGuid(),
        Topic = "Episode about nothing in particular",
        Brief = "No names dropped.",
        ReferencedTrackIdsJson = System.Text.Json.JsonSerializer.Serialize(trackIds.ToList()),
    };

    private static async Task<Guid> SeedKnowledgeAsync(
        DbFixture fixture, MetadataStatus status, string qid, string artist, string digest)
    {
        await using var db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
        }

        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = $"{artist} song",
            ImportedArtist = artist,
            Source = TrackSource.External,
            MetadataStatus = status,
            FilePath = $@"E:\music\{qid}.wav",
            CreatedAt = DateTime.UtcNow,
        };
        db.Tracks.Add(track);
        db.ExternalIds.Add(new ExternalId
        {
            Id = Guid.NewGuid(),
            OwnerType = MetadataOwnerType.Track,
            OwnerId = track.Id,
            Source = "Wikidata",
            EntityType = "Qid",
            Value = qid,
            Confidence = 1.0,
            CreatedAt = DateTime.UtcNow,
        });
        db.KnowledgeEntries.Add(new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            EntityKind = "artist",
            DisplayName = artist,
            Source = "Wikidata",
            SourceEntityId = qid,
            Digest = digest,
            RetrievedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return track.Id;
    }
}
