using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class EfTrackRepositoryTests
{
    [TestMethod]
    public async Task GetCandidatesAsync_LoadsArtistForAnnouncementPrompts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite(connection)
            .Options;

        var artistId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        await using (var db = new RadioDbContext(options))
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

        await using (var db = new RadioDbContext(options))
        {
            var repository = new EfTrackRepository(db);

            var candidates = await repository.GetCandidatesAsync(CancellationToken.None);

            var track = candidates.Single(candidate => candidate.Id == trackId);
            Assert.NotNull(track.Artist);
            Assert.Equal("Glass Harbor", track.Artist!.Name);
        }
    }
}
