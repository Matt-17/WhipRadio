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
public class EmergencyFallbackTrackServiceTests
{
    [TestMethod]
    public async Task TryCreateFallbackTrackAsync_ReturnsNullWhenNoPlayableGeneratedTracksExist()
    {
        await using var fixture = await DbFixture.CreateAsync();
        using var library = TempLibrary.Create();
        var service = CreateService(fixture, library.Root);

        var item = await service.TryCreateFallbackTrackAsync(null, CancellationToken.None);

        Assert.Null(item);
    }

    [TestMethod]
    public async Task TryCreateFallbackTrackAsync_ReturnsExistingTrackWithFallbackOrigin()
    {
        await using var fixture = await DbFixture.CreateAsync();
        using var library = TempLibrary.Create();
        var trackId = await SeedTrackAsync(fixture, library, "night-drive", playCount: 4);
        var service = CreateService(fixture, library.Root);

        var item = await service.TryCreateFallbackTrackAsync(null, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(trackId, item.ItemId);
        Assert.Equal(PlayoutItemType.Track, item.ItemType);
        Assert.Equal(PlayoutItemOrigin.Fallback, item.Origin);
    }

    [TestMethod]
    public async Task TryCreateFallbackTrackAsync_AvoidsJustFinishedTrackWhenAlternativeExists()
    {
        await using var fixture = await DbFixture.CreateAsync();
        using var library = TempLibrary.Create();
        var justFinishedId = await SeedTrackAsync(fixture, library, "just-finished", playCount: 0);
        var alternativeId = await SeedTrackAsync(fixture, library, "alternative", playCount: 1);
        var service = CreateService(fixture, library.Root);

        var justFinished = new PlayoutItem(
            PlayoutItemType.Track,
            justFinishedId,
            "library/tracks/just-finished.wav",
            "just-finished",
            90);

        var item = await service.TryCreateFallbackTrackAsync(justFinished, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(alternativeId, item.ItemId);
    }

    [TestMethod]
    public async Task TryCreateFallbackTrackAsync_AvoidsRecentFallbackWhenAlternativeExists()
    {
        await using var fixture = await DbFixture.CreateAsync();
        using var library = TempLibrary.Create();
        var recentId = await SeedTrackAsync(fixture, library, "recent", playCount: 0);
        var alternativeId = await SeedTrackAsync(fixture, library, "alternative", playCount: 1);
        await using (var db = await fixture.CreateDbContextAsync())
        {
            db.PlayLog.Add(new PlayLogEntry
            {
                PlayedAt = DateTime.UtcNow,
                ItemType = PlayoutItemType.Track,
                ItemId = recentId,
                DurationSeconds = 90,
                WasFallback = true,
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture, library.Root);

        var item = await service.TryCreateFallbackTrackAsync(null, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(alternativeId, item.ItemId);
    }

    private static EmergencyFallbackTrackService CreateService(DbFixture fixture, string dataRoot)
        => new(
            fixture,
            new QueueStateTracker(),
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            NullLogger<EmergencyFallbackTrackService>.Instance);

    private static async Task<Guid> SeedTrackAsync(
        DbFixture fixture,
        TempLibrary library,
        string name,
        int playCount)
    {
        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "tracks", $"{name}.wav");
        library.Write(relativePath);
        await using var db = await fixture.CreateDbContextAsync();
        db.Tracks.Add(new Track
        {
            Id = id,
            Title = name,
            Genre = "electronic",
            Subgenre = "synthwave",
            DurationSeconds = 90,
            FilePath = relativePath,
            CreatedAt = DateTime.UtcNow.AddMinutes(playCount),
            PlayCount = playCount,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class TempLibrary : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "whipradio-fallback-tests",
            Guid.NewGuid().ToString("N"));

        public static TempLibrary Create()
        {
            var library = new TempLibrary();
            Directory.CreateDirectory(library.Root);
            return library;
        }

        public void Write(string relativePath)
        {
            var absolutePath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllBytes(absolutePath, [1, 2, 3, 4]);
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
