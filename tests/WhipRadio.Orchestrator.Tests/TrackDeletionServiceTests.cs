using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class TrackDeletionServiceTests
{
    [TestMethod]
    public async Task QueuedDelete_RemovesTrackAnalysisAndFileAfterPlaybackCompletes()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = await TempLibrary.CreateWithTrackAsync(fixture);
        TrackDeletionService service = CreateService(fixture, library.Root);
        PlayoutItem item = library.PlayoutItem;

        service.MarkPlaybackStarted(item);

        TrackDeletionResult queued = await service.QueueForDeletionAsync(item.ItemId, CancellationToken.None);

        Assert.Equal(TrackDeletionStatus.Queued, queued.Status);
        Assert.True(service.IsPending(item.ItemId));
        Assert.True(service.IsTrackActive(item.ItemId));

        await service.MarkPlaybackCompletedAsync(item, CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.False(await db.Tracks.AnyAsync(t => t.Id == item.ItemId));
        Assert.False(await db.MediaAnalyses.AnyAsync(a => a.ItemId == item.ItemId));
        Assert.False(File.Exists(library.AbsolutePath));
        Assert.False(service.IsPending(item.ItemId));
        Assert.False(service.IsTrackActive(item.ItemId));
    }

    [TestMethod]
    public async Task QueuedDelete_WaitsForLastActivePlaybackReference()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempLibrary library = await TempLibrary.CreateWithTrackAsync(fixture);
        TrackDeletionService service = CreateService(fixture, library.Root);
        PlayoutItem item = library.PlayoutItem;

        service.MarkPlaybackStarted(item);
        service.MarkPlaybackStarted(item);
        await service.QueueForDeletionAsync(item.ItemId, CancellationToken.None);

        await service.MarkPlaybackCompletedAsync(item, CancellationToken.None);

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            Assert.True(await db.Tracks.AnyAsync(t => t.Id == item.ItemId));
        }

        Assert.True(File.Exists(library.AbsolutePath));
        Assert.True(service.IsTrackActive(item.ItemId));
        Assert.True(service.IsPending(item.ItemId));

        await service.MarkPlaybackCompletedAsync(item, CancellationToken.None);

        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.False(await verify.Tracks.AnyAsync(t => t.Id == item.ItemId));
        Assert.False(File.Exists(library.AbsolutePath));
        Assert.False(service.IsTrackActive(item.ItemId));
        Assert.False(service.IsPending(item.ItemId));
    }

    private static TrackDeletionService CreateService(DbFixture fixture, string root)
        => new(
            fixture,
            Options.Create(new RadioOptions { DataRoot = root }),
            NullLogger<TrackDeletionService>.Instance);

    private sealed class TempLibrary : IDisposable
    {
        private TempLibrary(string root, Guid trackId, string relativePath)
        {
            Root = root;
            TrackId = trackId;
            RelativePath = relativePath;
        }

        public string Root { get; }

        public Guid TrackId { get; }

        public string RelativePath { get; }

        public string AbsolutePath => Path.Combine(Root, RelativePath);

        public PlayoutItem PlayoutItem
            => new(PlayoutItemType.Track, TrackId, RelativePath, "Deferred Delete", 90);

        public static async Task<TempLibrary> CreateWithTrackAsync(DbFixture fixture)
        {
            string root = Path.Combine(Path.GetTempPath(), "whipradio-delete-tests", Guid.NewGuid().ToString("N"));
            Guid trackId = Guid.NewGuid();
            string relativePath = Path.Combine("library", "tracks", $"{trackId}.wav");
            string absolutePath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, [1, 2, 3, 4]);

            await using RadioDbContext db = fixture.CreateDbContext();
            db.Tracks.Add(new Track
            {
                Id = trackId,
                Title = "Deferred Delete",
                Genre = "test",
                Subgenre = "test",
                FilePath = relativePath,
                DurationSeconds = 90,
                CreatedAt = DateTime.UtcNow,
            });
            db.MediaAnalyses.Add(new MediaAnalysis
            {
                Id = Guid.NewGuid(),
                ItemType = PlayoutItemType.Track,
                ItemId = trackId,
                DurationSeconds = 90,
                AnalyzerVersion = 1,
                AnalyzedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            return new TempLibrary(root, trackId, relativePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
