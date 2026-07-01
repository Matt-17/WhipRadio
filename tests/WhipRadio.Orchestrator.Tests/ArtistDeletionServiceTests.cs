using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ArtistDeletionServiceTests
{
    [TestMethod]
    public async Task DeleteAsync_RemovesArtistAndMembersWhenArtistHasNoTracks()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var artistId = await AddArtistAsync(fixture, withTrack: false);
        var service = new ArtistDeletionService(fixture, new MusicProductionControl());

        var result = await service.DeleteAsync(artistId, CancellationToken.None);

        Assert.Equal(ArtistDeletionStatus.Deleted, result.Status);

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.False(await db.Artists.AnyAsync(a => a.Id == artistId));
        Assert.False(await db.ArtistMembers.AnyAsync(m => m.ArtistId == artistId));
    }

    [TestMethod]
    public async Task DeleteAsync_BlocksArtistWithTracks()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var artistId = await AddArtistAsync(fixture, withTrack: true);
        var service = new ArtistDeletionService(fixture, new MusicProductionControl());

        var result = await service.DeleteAsync(artistId, CancellationToken.None);

        Assert.Equal(ArtistDeletionStatus.HasTracks, result.Status);
        Assert.Equal(1, result.TrackCount);

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True(await db.Artists.AnyAsync(a => a.Id == artistId));
    }

    [TestMethod]
    public async Task DeleteAsync_BlocksArtistQueuedForProduction()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var artistId = await AddArtistAsync(fixture, withTrack: false);
        var control = new MusicProductionControl();
        control.RequestTrackFor(artistId);
        var service = new ArtistDeletionService(fixture, control);

        var result = await service.DeleteAsync(artistId, CancellationToken.None);

        Assert.Equal(ArtistDeletionStatus.InProduction, result.Status);

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True(await db.Artists.AnyAsync(a => a.Id == artistId));
    }

    private static async Task<Guid> AddArtistAsync(DbFixture fixture, bool withTrack)
    {
        var artistId = Guid.NewGuid();
        await using RadioDbContext db = fixture.CreateDbContext();
        db.Artists.Add(new Artist
        {
            Id = artistId,
            Name = "Empty Signal",
            Genre = "electronic",
            Subgenre = "testwave",
            StyleDescriptor = "Synthetic tests with a dry pulse.",
            CreatedAt = DateTime.UtcNow,
            Members =
            {
                new ArtistMember
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    Name = "Mira Test",
                    Role = "synth",
                    Biography = "Keeps the test pulse steady.",
                    VoiceCreationPrompt = "Clear studio voice.",
                },
            },
        });

        if (withTrack)
        {
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                ArtistId = artistId,
                Title = "Cannot Delete Yet",
                Genre = "electronic",
                Subgenre = "testwave",
                Style = "Synthetic tests with a dry pulse.",
                FilePath = "library/tracks/test.wav",
                DurationSeconds = 90,
                GenerationPrompt = "test",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return artistId;
    }
}
