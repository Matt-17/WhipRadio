using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Metadata;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Snapshot for the Archive status endpoint.</summary>
public sealed record LibraryScanStatus(
    DateTime? LastScanUtc,
    int ConfiguredFolders,
    int LastScanImported,
    int LastScanMissing,
    bool ScanRunning);

/// <summary>
/// Imports existing music from the read-only external folders configured in
/// appsettings (<c>Library:ExternalMusicFolders</c>, Phase 6a). Incremental:
/// unchanged files (path + size + mtime) are skipped, changed files re-hashed,
/// duplicates (by SHA-256) imported once, vanished files flagged
/// <see cref="Track.FileMissing"/> and reappearing ones cleared. The folders
/// are never written to — WhipRadio only ever reads these files.
/// </summary>
public sealed class LibraryImportService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IFileTagReader tagReader,
    IOptions<LibraryOptions> libraryOptions,
    TimeProvider timeProvider,
    ILogger<LibraryImportService> logger) : BackgroundService
{
    private static readonly string[] SupportedExtensions = [".wav", ".mp3"];

    private readonly SemaphoreSlim _rescanSignal = new(0, 1);
    private volatile LibraryScanStatus _status = new(null, 0, 0, 0, false);

    public LibraryScanStatus Status => _status;

    /// <summary>Wakes the scan loop immediately (Archive page "Rescan").</summary>
    public void RequestRescan()
    {
        try
        {
            _rescanSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A rescan is already pending.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the station come up before the first scan hits the disk.
        await DelayOrSignalAsync(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Library scan failed ({Reason})", ex.GetBaseException().Message);
            }

            var minutes = Math.Max(1, libraryOptions.Value.RescanMinutes);
            await DelayOrSignalAsync(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    private async Task DelayOrSignalAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _rescanSignal.WaitAsync(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Exposed for tests and the manual rescan endpoint.</summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var folders = (libraryOptions.Value.ExternalMusicFolders ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(Path.GetFullPath)
            .ToList();
        _status = _status with { ScanRunning = true, ConfiguredFolders = folders.Count };

        var imported = 0;
        var missing = 0;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var externalTracks = await db.Tracks
                .Where(t => t.Source == TrackSource.External)
                .ToListAsync(ct);
            var byPath = externalTracks.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
            var knownHashes = await db.Tracks.AsNoTracking()
                .Where(t => t.Source != TrackSource.Generated && t.FileHash != null)
                .Select(t => t.FileHash!)
                .ToListAsync(ct);
            var seenHashes = new HashSet<string>(knownHashes, StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    logger.LogWarning("External music folder does not exist: {Folder}", folder);
                    continue;
                }

                foreach (var file in EnumerateAudioFiles(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    seenPaths.Add(file);

                    var info = new FileInfo(file);
                    if (byPath.TryGetValue(file, out var existing))
                    {
                        existing.FileMissing = false;
                        var unchanged = existing.FileSizeBytes == info.Length
                            && existing.FileModifiedUtc == info.LastWriteTimeUtc;
                        if (unchanged)
                        {
                            continue;
                        }

                        // File changed on disk: refresh identity but keep votes/plays.
                        var newHash = await HashFileAsync(file, ct);
                        existing.FileHash = newHash;
                        existing.FileSizeBytes = info.Length;
                        existing.FileModifiedUtc = info.LastWriteTimeUtc;
                        ApplyIdentity(existing, file);
                        existing.MetadataStatus = MetadataStatus.LocalOnly;
                        existing.MetadataConfidence = null;
                        existing.LastEnrichmentAttemptUtc = null;
                        seenHashes.Add(newHash);
                        continue;
                    }

                    var hash = await HashFileAsync(file, ct);
                    if (!seenHashes.Add(hash))
                    {
                        logger.LogDebug("Skipping duplicate audio (same hash): {File}", file);
                        continue;
                    }

                    var track = new Track
                    {
                        Id = Guid.NewGuid(),
                        Source = TrackSource.External,
                        Backend = "library",
                        FilePath = file,
                        FileHash = hash,
                        FileSizeBytes = info.Length,
                        FileModifiedUtc = info.LastWriteTimeUtc,
                        MetadataStatus = MetadataStatus.LocalOnly,
                        HasVocals = true,
                        CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
                    };
                    ApplyIdentity(track, file);
                    db.Tracks.Add(track);
                    byPath[file] = track;
                    imported++;
                }
            }

            // Vanished files (or folders removed from configuration) leave
            // rotation via FileMissing but keep their rows; the file itself is
            // never touched and IsRetired stays vote/operator-owned.
            foreach (var track in externalTracks)
            {
                if (!track.FileMissing && !seenPaths.Contains(track.FilePath))
                {
                    track.FileMissing = true;
                    missing++;
                }
            }

            await db.SaveChangesAsync(ct);
            if (imported > 0 || missing > 0)
            {
                logger.LogInformation(
                    "Library scan: {Imported} imported, {Missing} missing across {Folders} folder(s)",
                    imported, missing, folders.Count);
            }
        }
        finally
        {
            _status = new LibraryScanStatus(
                timeProvider.GetUtcNow().UtcDateTime, folders.Count, imported, missing, false);
        }
    }

    private void ApplyIdentity(Track track, string file)
    {
        var tags = tagReader.Read(file);
        var clues = FilenameHeuristics.Parse(file);

        track.Title = tags.Title ?? clues.Title ?? Path.GetFileNameWithoutExtension(file);
        track.ImportedArtist = tags.Artist ?? tags.AlbumArtist ?? clues.Artist;
        track.ImportedAlbum = tags.Album ?? clues.Album;
        track.ImportedYear = tags.Year;
        track.Genre = tags.Genre ?? string.Empty;
        track.Language = string.Empty;
        if (tags.DurationSeconds is { } duration and > 0)
        {
            // Tag-derived; the analysis pipeline replaces it with the probed value.
            track.DurationSeconds = duration;
        }
    }

    private static IEnumerable<string> EnumerateAudioFiles(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => SupportedExtensions.Contains(
                Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));

    private static async Task<string> HashFileAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
