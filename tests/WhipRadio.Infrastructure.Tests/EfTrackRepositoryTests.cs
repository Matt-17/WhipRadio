using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.TestSupport;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class EfTrackRepositoryTests
{
    [TestMethod]
    public async Task GetCandidatesAsync_LoadsArtistForAnnouncementPrompts()
    {
        await using var fixture = await DbFixture.CreateAsync();

        var artistId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Artists.Add(new Artist
            {
                Id = artistId,
                Name = "Glass Harbor",
                Genre = "synth pop",
                Subgenre = "night drive",
                CreatedAt = DateTime.UtcNow,
            });
            db.Tracks.Add(new Track
            {
                Id = trackId,
                Title = "Afterimage Arcade",
                ArtistId = artistId,
                Genre = "synth pop",
                Subgenre = "night drive",
                Style = "warm arpeggios",
                FilePath = "library/music/afterimage.wav",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EfTrackRepository(db);

            var candidates = await repository.GetCandidatesAsync(CancellationToken.None);

            var track = candidates.Single(candidate => candidate.Id == trackId);
            Assert.NotNull(track.Artist);
            Assert.Equal("Glass Harbor", track.Artist!.Name);
        }
    }

    [TestMethod]
    public async Task GetTrackIdsPlayedSinceAsync_ReturnsOnlyTracksInWindow()
    {
        await using var fixture = await DbFixture.CreateAsync();

        var oldTrack = Guid.NewGuid();
        var newTrack = Guid.NewGuid();
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = DateTime.UtcNow.AddMinutes(-60),
                ItemType = PlayoutItemType.Track,
                ItemId = oldTrack,
            });
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = DateTime.UtcNow.AddMinutes(-10),
                ItemType = PlayoutItemType.Track,
                ItemId = newTrack,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EfTrackRepository(db);
            var ids = await repository.GetTrackIdsPlayedSinceAsync(cutoff, 50, CancellationToken.None);

            Assert.Equal(1, ids.Count);
            Assert.Equal(newTrack, ids[0]);
        }
    }

    [TestMethod]
    public async Task GetRecentPlayedRefsAsync_ReturnsArtistAndSubgenreMetadata()
    {
        await using var fixture = await DbFixture.CreateAsync();

        var artistId = Guid.NewGuid();
        var trackId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Artists.Add(new Artist
            {
                Id = artistId,
                Name = "Glass Harbor",
                Genre = "synth pop",
                Subgenre = "night drive",
                CreatedAt = DateTime.UtcNow,
            });
            db.Tracks.Add(new Track
            {
                Id = trackId,
                Title = "Afterimage Arcade",
                ArtistId = artistId,
                Genre = "synth pop",
                Subgenre = "night drive",
                FilePath = "library/music/afterimage.wav",
                CreatedAt = DateTime.UtcNow,
            });
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = DateTime.UtcNow,
                ItemType = PlayoutItemType.Track,
                ItemId = trackId,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new EfTrackRepository(db);
            var refs = await repository.GetRecentPlayedRefsAsync(10, CancellationToken.None);

            Assert.Equal(1, refs.Count);
            Assert.Equal(trackId, refs[0].TrackId);
            Assert.Equal(artistId, refs[0].ArtistId);
            Assert.Equal("night drive", refs[0].Subgenre);
        }
    }
}
