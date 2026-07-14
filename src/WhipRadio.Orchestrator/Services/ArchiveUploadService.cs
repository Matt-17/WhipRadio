using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Metadata;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public enum ArchiveUploadOutcome
{
    Stored,
    Duplicate,
    TooLarge,
    NotAudio,
    Empty,
}

public sealed record ArchiveUploadResult(
    ArchiveUploadOutcome Outcome,
    Track? Track = null,
    Guid? ExistingTrackId = null,
    string? ExistingTitle = null);

/// <summary>
/// Stores one uploaded audio file in the archive: streams it to
/// <c>data/archive/uploads/</c> while hashing, enforces the size cap, sniffs
/// WAV/MP3 magic bytes, dedupes by SHA-256 against all imported tracks, and
/// creates the <see cref="Track"/> row (Source=Uploaded, LocalOnly).
/// </summary>
public sealed class ArchiveUploadService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IFileTagReader tagReader,
    IOptions<RadioOptions> radioOptions,
    IOptions<LibraryOptions> libraryOptions,
    TimeProvider timeProvider)
{
    public async Task<ArchiveUploadResult> StoreAsync(Stream content, string fileName, CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
        {
            return new ArchiveUploadResult(ArchiveUploadOutcome.NotAudio);
        }

        var uploadsDirectory = radioOptions.Value.ArchiveUploadsDirectory;
        Directory.CreateDirectory(uploadsDirectory);

        var maxBytes = libraryOptions.Value.MaxUploadBytes;
        var trackId = Guid.NewGuid();
        var tempPath = Path.Combine(uploadsDirectory, $"{trackId}.tmp");
        try
        {
            long totalBytes = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > maxBytes)
                    {
                        return new ArchiveUploadResult(ArchiveUploadOutcome.TooLarge);
                    }

                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (totalBytes == 0)
            {
                return new ArchiveUploadResult(ArchiveUploadOutcome.Empty);
            }

            if (!LooksLikeAudio(tempPath, extension))
            {
                return new ArchiveUploadResult(ArchiveUploadOutcome.NotAudio);
            }

            var hash = Convert.ToHexString(sha.GetHashAndReset());
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Tracks.AsNoTracking()
                .Where(t => t.FileHash == hash && t.Source != TrackSource.Generated)
                .Select(t => new { t.Id, t.Title })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return new ArchiveUploadResult(
                    ArchiveUploadOutcome.Duplicate, ExistingTrackId: existing.Id, ExistingTitle: existing.Title);
            }

            var finalPath = Path.Combine(uploadsDirectory, $"{trackId}{extension}");
            File.Move(tempPath, finalPath);

            var tags = tagReader.Read(finalPath);
            var clues = FilenameHeuristics.Parse(fileName);
            var info = new FileInfo(finalPath);
            var track = new Track
            {
                Id = trackId,
                Source = TrackSource.Uploaded,
                Backend = "library",
                FilePath = Path.Combine("archive", "uploads", $"{trackId}{extension}"),
                FileHash = hash,
                FileSizeBytes = info.Length,
                FileModifiedUtc = info.LastWriteTimeUtc,
                MetadataStatus = MetadataStatus.LocalOnly,
                HasVocals = true,
                Title = tags.Title ?? clues.Title ?? Path.GetFileNameWithoutExtension(fileName),
                ImportedArtist = tags.Artist ?? tags.AlbumArtist ?? clues.Artist,
                ImportedAlbum = tags.Album ?? clues.Album,
                ImportedYear = tags.Year,
                Genre = tags.Genre ?? string.Empty,
                Language = string.Empty,
                DurationSeconds = tags.DurationSeconds ?? 0,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync(ct);
            return new ArchiveUploadResult(ArchiveUploadOutcome.Stored, track);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Magic-byte sniff: RIFF/WAVE for .wav; ID3 tag or MPEG frame sync for .mp3.</summary>
    private static bool LooksLikeAudio(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < header.Length)
        {
            return false;
        }

        return extension switch
        {
            ".wav" => header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),
            ".mp3" => header[..3].SequenceEqual("ID3"u8)
                || (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0),
            _ => false,
        };
    }
}
