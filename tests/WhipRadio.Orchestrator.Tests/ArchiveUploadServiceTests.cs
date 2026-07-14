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
public class ArchiveUploadServiceTests
{
    [TestMethod]
    public async Task Store_ValidWav_CreatesUploadedTrackUnderTheDataRoot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot);
            var result = await service.StoreAsync(
                new MemoryStream(Wav(seed: 1)), "Massive Attack - Teardrop.wav", CancellationToken.None);

            Assert.Equal(ArchiveUploadOutcome.Stored, result.Outcome);
            var track = result.Track!;
            Assert.Equal(TrackSource.Uploaded, track.Source);
            Assert.Equal(MetadataStatus.LocalOnly, track.MetadataStatus);
            Assert.Equal("Teardrop", track.Title);
            Assert.Equal("Massive Attack", track.ImportedArtist);
            Assert.False(Path.IsPathRooted(track.FilePath), "uploads keep the relative-path invariant");
            var absolute = Path.Combine(dataRoot, track.FilePath);
            Assert.True(File.Exists(absolute), $"uploaded audio missing at {absolute}");

            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.Tracks.CountAsync(t => t.Id == track.Id));
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Store_DuplicateContent_IsRejectedWithTheExistingTrack()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot);
            var first = await service.StoreAsync(new MemoryStream(Wav(seed: 2)), "one.wav", CancellationToken.None);
            var second = await service.StoreAsync(new MemoryStream(Wav(seed: 2)), "two.wav", CancellationToken.None);

            Assert.Equal(ArchiveUploadOutcome.Duplicate, second.Outcome);
            Assert.Equal(first.Track!.Id, second.ExistingTrackId);
            // The rejected duplicate leaves no file behind.
            Assert.Equal(1, Directory.EnumerateFiles(Path.Combine(dataRoot, "archive", "uploads")).Count());
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Store_OversizeAndNonAudio_AreRejectedWithoutLeftovers()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, maxUploadBytes: 1024);

            var tooBig = await service.StoreAsync(
                new MemoryStream(Wav(seed: 3, frames: 4000)), "big.wav", CancellationToken.None);
            Assert.Equal(ArchiveUploadOutcome.TooLarge, tooBig.Outcome);

            var notAudio = await service.StoreAsync(
                new MemoryStream("this is not audio at all"u8.ToArray()), "fake.wav", CancellationToken.None);
            Assert.Equal(ArchiveUploadOutcome.NotAudio, notAudio.Outcome);

            var wrongExtension = await service.StoreAsync(
                new MemoryStream(Wav(seed: 3, frames: 100)), "song.flac", CancellationToken.None);
            Assert.Equal(ArchiveUploadOutcome.NotAudio, wrongExtension.Outcome);

            var uploadsDir = Path.Combine(dataRoot, "archive", "uploads");
            Assert.True(!Directory.Exists(uploadsDir) || !Directory.EnumerateFiles(uploadsDir).Any());
            await using var db = fixture.CreateDbContext();
            Assert.Equal(0, await db.Tracks.CountAsync());
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Delete_UploadedRemovesTheFile_ExternalNeverTouchesIt()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = TestRoot();
        var externalFolder = TestRoot();
        try
        {
            // Uploaded track through the real service.
            var service = CreateService(fixture, dataRoot);
            var stored = await service.StoreAsync(new MemoryStream(Wav(seed: 4)), "up.wav", CancellationToken.None);
            var uploadedFile = Path.Combine(dataRoot, stored.Track!.FilePath);

            // External track with an absolute path outside the data root.
            var externalFile = Path.Combine(externalFolder, "keep-me.wav");
            Directory.CreateDirectory(externalFolder);
            await File.WriteAllBytesAsync(externalFile, Wav(seed: 5));
            Guid externalId;
            await using (var db = fixture.CreateDbContext())
            {
                var external = new Track
                {
                    Id = Guid.NewGuid(),
                    Source = TrackSource.External,
                    Backend = "library",
                    Title = "Keep Me",
                    FilePath = externalFile,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Tracks.Add(external);
                await db.SaveChangesAsync();
                externalId = external.Id;
            }

            var deletions = new TrackDeletionService(
                fixture,
                Options.Create(new RadioOptions { DataRoot = dataRoot }),
                NullLogger<TrackDeletionService>.Instance);

            Assert.Equal(TrackDeletionStatus.Deleted, (await deletions.DeleteNowAsync(stored.Track.Id, CancellationToken.None)).Status);
            Assert.False(File.Exists(uploadedFile), "uploaded audio must be deleted with its row");

            Assert.Equal(TrackDeletionStatus.Deleted, (await deletions.DeleteNowAsync(externalId, CancellationToken.None)).Status);
            Assert.True(File.Exists(externalFile), "external-folder audio must never be deleted");

            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(0, await db.Tracks.CountAsync());
            }
        }
        finally
        {
            DeleteRoot(dataRoot);
            DeleteRoot(externalFolder);
        }
    }

    private static ArchiveUploadService CreateService(
        DbFixture fixture, string dataRoot, long maxUploadBytes = 100L * 1024 * 1024)
        => new(
            fixture,
            new EmptyTagReader(),
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            Options.Create(new LibraryOptions { MaxUploadBytes = maxUploadBytes }),
            TimeProvider.System);

    private static byte[] Wav(byte seed, int frames = 800)
    {
        var pcm = new byte[frames * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(seed * 37 + i);
        }

        return WavFile.WrapPcm16(pcm, 8000, 1);
    }

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "whipradio-archive-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EmptyTagReader : IFileTagReader
    {
        public FileTags Read(string absolutePath) => new();
    }
}
