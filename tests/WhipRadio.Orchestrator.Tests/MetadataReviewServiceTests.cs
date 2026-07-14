using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class MetadataReviewServiceTests
{
    [TestMethod]
    public async Task Accept_AppliesCandidateVerifiesTrackAndRejectsSiblings()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (trackId, acceptedId, siblingId) = await SeedAmbiguousTrackAsync(fixture);
        var service = new MetadataReviewService(fixture, TimeProvider.System);

        Assert.True(await service.AcceptCandidateAsync(trackId, acceptedId, CancellationToken.None));

        await using var db = fixture.CreateDbContext();
        var track = await db.Tracks.AsNoTracking().SingleAsync();
        Assert.Equal(MetadataStatus.Verified, track.MetadataStatus);
        Assert.Equal("Teardrop", track.Title);
        Assert.Equal("Massive Attack", track.ImportedArtist);
        Assert.Equal("Mezzanine", track.ImportedAlbum);
        Assert.Equal(1998, track.ImportedYear);

        var candidates = await db.MetadataCandidates.AsNoTracking().ToListAsync();
        Assert.Equal(CandidateStatus.Accepted, candidates.Single(c => c.Id == acceptedId).Status);
        Assert.Equal(CandidateStatus.Rejected, candidates.Single(c => c.Id == siblingId).Status);

        var claims = await db.MetadataClaims.AsNoTracking().Where(c => c.OwnerId == trackId).ToListAsync();
        Assert.Contains(claims, c => c is { Source: "User", FieldName: "Title", Value: "Teardrop", IsApplied: true });
        Assert.Contains(await db.ExternalIds.AsNoTracking().ToListAsync(),
            e => e is { EntityType: "Recording", Value: "rec-accept" });
    }

    [TestMethod]
    public async Task Reject_LastPendingCandidate_DowngradesTheTrackToNeedsReview()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (trackId, first, second) = await SeedAmbiguousTrackAsync(fixture);
        var service = new MetadataReviewService(fixture, TimeProvider.System);

        Assert.True(await service.RejectCandidateAsync(trackId, first, CancellationToken.None));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(MetadataStatus.Ambiguous, (await db.Tracks.AsNoTracking().SingleAsync()).MetadataStatus);
        }

        Assert.True(await service.RejectCandidateAsync(trackId, second, CancellationToken.None));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(MetadataStatus.NeedsReview, (await db.Tracks.AsNoTracking().SingleAsync()).MetadataStatus);
        }
    }

    [TestMethod]
    public async Task KeepLocal_PinsTheTrackAndRejectsPendingCandidates()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (trackId, _, _) = await SeedAmbiguousTrackAsync(fixture);
        var service = new MetadataReviewService(fixture, TimeProvider.System);

        Assert.True(await service.KeepLocalAsync(trackId, CancellationToken.None));

        await using var db = fixture.CreateDbContext();
        var track = await db.Tracks.AsNoTracking().SingleAsync();
        Assert.Equal(MetadataStatus.Rejected, track.MetadataStatus);
        Assert.Equal("Teardrop Bootleg", track.Title);
        Assert.True(await db.MetadataCandidates.AsNoTracking().AllAsync(c => c.Status == CandidateStatus.Rejected));
    }

    [TestMethod]
    public async Task AcceptAllMatched_PromotesOnlyMatchedImportedTracks()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Tracks.Add(NewTrack("matched", TrackSource.Uploaded, MetadataStatus.Matched));
            db.Tracks.Add(NewTrack("auto", TrackSource.External, MetadataStatus.AutoMatched));
            db.Tracks.Add(NewTrack("ambiguous", TrackSource.External, MetadataStatus.Ambiguous));
            db.Tracks.Add(NewTrack("generated", TrackSource.Generated, MetadataStatus.None));
            await db.SaveChangesAsync();
        }

        var service = new MetadataReviewService(fixture, TimeProvider.System);
        var promoted = await service.AcceptAllMatchedAsync(CancellationToken.None);

        Assert.Equal(2, promoted);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(2, await db.Tracks.CountAsync(t => t.MetadataStatus == MetadataStatus.Verified));
            Assert.Equal(MetadataStatus.Ambiguous,
                (await db.Tracks.AsNoTracking().SingleAsync(t => t.Title == "ambiguous")).MetadataStatus);
            Assert.Equal(MetadataStatus.None,
                (await db.Tracks.AsNoTracking().SingleAsync(t => t.Title == "generated")).MetadataStatus);
        }
    }

    private static Track NewTrack(string title, TrackSource source, MetadataStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Source = source,
        MetadataStatus = status,
        FilePath = source == TrackSource.Generated ? "library/tracks/x.wav" : @"E:\music\x.wav",
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task<(Guid TrackId, Guid FirstCandidateId, Guid SecondCandidateId)> SeedAmbiguousTrackAsync(
        DbFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "Teardrop Bootleg",
            ImportedArtist = "M. Attack",
            Source = TrackSource.External,
            MetadataStatus = MetadataStatus.Ambiguous,
            FilePath = @"E:\music\bootleg.wav",
            CreatedAt = DateTime.UtcNow,
        };
        db.Tracks.Add(track);

        var first = new MetadataCandidate
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            SourceEntityId = "rec-accept",
            DisplayTitle = "Teardrop",
            DisplayArtist = "Massive Attack",
            DisplayAlbum = "Mezzanine",
            DisplayYear = 1998,
            ArtistEntityId = "art-1",
            Score = 0.7,
            Status = CandidateStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        var second = new MetadataCandidate
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            SourceEntityId = "rec-other",
            DisplayTitle = "Teardrop (live)",
            DisplayArtist = "Massive Attack",
            Score = 0.6,
            Status = CandidateStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.MetadataCandidates.AddRange(first, second);
        await db.SaveChangesAsync();
        return (track.Id, first.Id, second.Id);
    }
}
