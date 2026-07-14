using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Metadata;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class LibraryImportServiceTests
{
    [TestMethod]
    public async Task Scan_ImportsWavAndMp3AsExternalLocalOnly_AndIsIdempotent()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var folder = CreateFolder();
        try
        {
            WriteWav(Path.Combine(folder, "Massive Attack - Teardrop.wav"), seed: 1);
            WriteFakeMp3(Path.Combine(folder, "01 - Angel.mp3"), seed: 2);
            var service = CreateService(fixture, folder);

            await service.ScanAsync(CancellationToken.None);
            await service.ScanAsync(CancellationToken.None); // rescan must not duplicate

            await using var db = fixture.CreateDbContext();
            var tracks = await db.Tracks.AsNoTracking().OrderBy(t => t.Title).ToListAsync();
            Assert.Equal(2, tracks.Count);
            Assert.All(tracks, t =>
            {
                Assert.Equal(TrackSource.External, t.Source);
                Assert.Equal(MetadataStatus.LocalOnly, t.MetadataStatus);
                Assert.Equal("library", t.Backend);
                Assert.True(Path.IsPathRooted(t.FilePath), "external tracks keep their absolute path");
                Assert.NotNull(t.FileHash);
                Assert.False(t.IsRetired);
                Assert.True(t.HasVocals);
            });
            Assert.Equal("Angel", tracks[0].Title);
            Assert.Equal("Teardrop", tracks[1].Title);
            Assert.Equal("Massive Attack", tracks[1].ImportedArtist);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task Scan_ImportsDuplicateContentOnlyOnce()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var folder = CreateFolder();
        try
        {
            WriteWav(Path.Combine(folder, "song.wav"), seed: 7);
            File.Copy(Path.Combine(folder, "song.wav"), Path.Combine(folder, "copy of song.wav"));
            var service = CreateService(fixture, folder);

            await service.ScanAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.Tracks.CountAsync());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task Scan_ChangedFileIsRehashedAndResetToLocalOnly()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var folder = CreateFolder();
        try
        {
            var file = Path.Combine(folder, "song.wav");
            WriteWav(file, seed: 3);
            var service = CreateService(fixture, folder);
            await service.ScanAsync(CancellationToken.None);

            string firstHash;
            await using (var db = fixture.CreateDbContext())
            {
                var track = await db.Tracks.SingleAsync();
                firstHash = track.FileHash!;
                track.MetadataStatus = MetadataStatus.Verified; // pretend it was reviewed
                await db.SaveChangesAsync();
            }

            WriteWav(file, seed: 4); // different audio content
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            await service.ScanAsync(CancellationToken.None);

            await using (var db = fixture.CreateDbContext())
            {
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.NotEqual(firstHash, track.FileHash);
                Assert.Equal(MetadataStatus.LocalOnly, track.MetadataStatus);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task Scan_FlagsVanishedFilesMissingAndClearsReappearingOnes_WithoutTouchingRetired()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var folder = CreateFolder();
        try
        {
            var file = Path.Combine(folder, "song.wav");
            WriteWav(file, seed: 5);
            var service = CreateService(fixture, folder);
            await service.ScanAsync(CancellationToken.None);

            // Vote/operator retirement must survive rescans of an unchanged file.
            await using (var db = fixture.CreateDbContext())
            {
                (await db.Tracks.SingleAsync()).IsRetired = true;
                await db.SaveChangesAsync();
            }

            var bytes = await File.ReadAllBytesAsync(file);
            File.Delete(file);
            await service.ScanAsync(CancellationToken.None);
            await using (var db = fixture.CreateDbContext())
            {
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.True(track.FileMissing);
                Assert.True(track.IsRetired);
            }

            await File.WriteAllBytesAsync(file, bytes);
            await service.ScanAsync(CancellationToken.None);
            await using (var db = fixture.CreateDbContext())
            {
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.False(track.FileMissing);
                Assert.True(track.IsRetired, "rescan must never un-retire a track");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task Scan_NeverWritesIntoTheExternalFolder()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var folder = CreateFolder();
        try
        {
            var file = Path.Combine(folder, "song.wav");
            WriteWav(file, seed: 6);
            var contentBefore = await File.ReadAllBytesAsync(file);
            var mtimeBefore = File.GetLastWriteTimeUtc(file);

            var service = CreateService(fixture, folder);
            await service.ScanAsync(CancellationToken.None);

            Assert.Equal(1, Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Count());
            Assert.Equal(contentBefore, await File.ReadAllBytesAsync(file));
            Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static LibraryImportService CreateService(DbFixture fixture, string folder)
        => new(
            fixture,
            new EmptyTagReader(),
            Options.Create(new LibraryOptions { ExternalMusicFolders = [folder] }),
            TimeProvider.System,
            NullLogger<LibraryImportService>.Instance);

    private static string CreateFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(), "whipradio-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteWav(string path, byte seed)
    {
        var pcm = new byte[8000];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(seed * 31 + i);
        }

        File.WriteAllBytes(path, WavFile.WrapPcm16(pcm, 8000, 1));
    }

    private static void WriteFakeMp3(string path, byte seed)
    {
        // The importer never decodes audio itself — any bytes with the right
        // extension exercise discovery/hashing.
        var bytes = new byte[2048];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed * 17 + i);
        }

        File.WriteAllBytes(path, bytes);
    }

    private sealed class EmptyTagReader : IFileTagReader
    {
        public FileTags Read(string absolutePath) => new();
    }
}
