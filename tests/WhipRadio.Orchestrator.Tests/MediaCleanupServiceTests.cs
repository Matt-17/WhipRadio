using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class MediaCleanupServiceTests
{
    [TestMethod]
    public async Task DeleteOrphanLibraryFilesAsync_DeletesUnreferencedAnnouncementAndTrackFiles()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        await library.WriteAsync("library/announcements/kept.wav", [1, 2, 3]);
        await library.WriteAsync("library/announcements/orphan.wav", [1, 2, 3, 4]);
        await library.WriteAsync("library/tracks/kept.wav", [1, 2, 3, 4, 5]);
        await library.WriteAsync("library/tracks/orphan.wav", [1, 2]);

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            db.Moderators.Add(new Moderator
            {
                Id = 1,
                Name = "Test Host",
                Slug = "test-host",
                PersonaPrompt = "Test host.",
                Style = "steady",
            });
            db.Announcements.Add(new Announcement
            {
                Id = Guid.NewGuid(),
                ModeratorId = 1,
                Kind = AnnouncementKind.StationId,
                FilePath = "library/announcements/kept.wav",
                CreatedAt = DateTime.UtcNow,
            });
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                Title = "Kept",
                Genre = "test",
                Subgenre = "test",
                FilePath = "library/tracks/kept.wav",
                DurationSeconds = 90,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        MediaCleanupService service = CreateService(fixture, library.Root);

        var result = await service.DeleteOrphanLibraryFilesAsync(CancellationToken.None);

        Assert.Equal(1, result.AnnouncementFilesDeleted);
        Assert.Equal(1, result.TrackFilesDeleted);
        Assert.Equal(6, result.BytesDeleted);
        Assert.Empty(result.FailedFiles);
        Assert.True(File.Exists(library.PathFor("library/announcements/kept.wav")));
        Assert.False(File.Exists(library.PathFor("library/announcements/orphan.wav")));
        Assert.True(File.Exists(library.PathFor("library/tracks/kept.wav")));
        Assert.False(File.Exists(library.PathFor("library/tracks/orphan.wav")));
    }

    [TestMethod]
    public async Task DeleteOrphanLibraryFilesAsync_IgnoresFilesOutsideLibraryMediaFolders()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        await library.WriteAsync("library/jingles/orphan.wav", [1, 2, 3]);

        MediaCleanupService service = CreateService(fixture, library.Root);

        var result = await service.DeleteOrphanLibraryFilesAsync(CancellationToken.None);

        Assert.Equal(0, result.AnnouncementFilesDeleted);
        Assert.Equal(0, result.TrackFilesDeleted);
        Assert.Equal(0, result.BytesDeleted);
        Assert.True(File.Exists(library.PathFor("library/jingles/orphan.wav")));
    }

    [TestMethod]
    public async Task PlanOrphanLibraryFilesAsync_CountsUnreferencedFilesWithoutDeletingThem()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = new();
        await library.WriteAsync("library/announcements/orphan.wav", [1, 2, 3, 4]);
        await library.WriteAsync("library/tracks/orphan.wav", [1, 2]);

        MediaCleanupService service = CreateService(fixture, library.Root);

        var plan = await service.PlanOrphanLibraryFilesAsync(CancellationToken.None);

        Assert.Equal(1, plan.AnnouncementFiles);
        Assert.Equal(1, plan.TrackFiles);
        Assert.Equal(6, plan.BytesToDelete);
        Assert.True(File.Exists(library.PathFor("library/announcements/orphan.wav")));
        Assert.True(File.Exists(library.PathFor("library/tracks/orphan.wav")));
    }

    private static MediaCleanupService CreateService(DbFixture fixture, string root)
        => new(
            fixture,
            Options.Create(new RadioOptions { DataRoot = root }),
            NullLogger<MediaCleanupService>.Instance);

    private sealed class TempLibrary : IDisposable
    {
        public TempLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "whipradio-cleanup-tests", Guid.NewGuid().ToString("N"));
        }

        public string Root { get; }

        public string PathFor(string relativePath) => Path.Combine(Root, relativePath);

        public async Task WriteAsync(string relativePath, byte[] bytes)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            DbContextOptions<RadioDbContext> options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (RadioDbContext db = new(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new DbFixture(connection, options);
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
