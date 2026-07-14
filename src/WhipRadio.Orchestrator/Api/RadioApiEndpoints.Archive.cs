using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    /// <summary>
    /// Archive = imported real music (Phase 6a). External-folder tracks are
    /// read-only guests here — no endpoint ever deletes or modifies their
    /// files; only uploads (stored under the data root) can be deleted.
    /// </summary>
    private static void MapArchive(RouteGroupBuilder api)
    {
        api.MapGet("/archive", async (RadioDbContext db, TrackDeletionService deletions, CancellationToken ct) =>
        {
            var tracks = await db.Tracks.AsNoTracking()
                .Where(t => t.Source != TrackSource.Generated)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync(ct);
            var pendingCandidates = await db.MetadataCandidates.AsNoTracking()
                .Where(c => c.Status == Core.Entities.Metadata.CandidateStatus.Pending)
                .GroupBy(c => c.TrackId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            return Results.Ok(tracks
                .Select(t => ToArchiveDto(t, deletions.IsPending(t.Id), pendingCandidates.GetValueOrDefault(t.Id)))
                .ToList());
        });

        api.MapGet("/archive/{trackId:guid}/candidates", async (
            Guid trackId, RadioDbContext db, CancellationToken ct) =>
        {
            var candidates = await db.MetadataCandidates.AsNoTracking()
                .Where(c => c.TrackId == trackId)
                .OrderByDescending(c => c.Score)
                .ToListAsync(ct);
            return Results.Ok(candidates.Select(c => new MetadataCandidateDto(
                c.Id,
                c.DisplayTitle,
                c.DisplayArtist,
                c.DisplayAlbum,
                c.DisplayYear,
                c.Score,
                ParseReasons(c.ReasonsJson),
                c.Status.ToString())).ToList());
        });

        api.MapPost("/archive/{trackId:guid}/candidates/{candidateId:guid}/accept", async (
            Guid trackId, Guid candidateId, MetadataReviewService review,
            IProductionUpdatePublisher publisher, CancellationToken ct) =>
        {
            if (!await review.AcceptCandidateAsync(trackId, candidateId, ct))
            {
                return Results.NotFound();
            }

            await publisher.PublishArchiveChangedAsync(ct);
            return Results.Ok();
        });

        api.MapPost("/archive/{trackId:guid}/candidates/{candidateId:guid}/reject", async (
            Guid trackId, Guid candidateId, MetadataReviewService review,
            IProductionUpdatePublisher publisher, CancellationToken ct) =>
        {
            if (!await review.RejectCandidateAsync(trackId, candidateId, ct))
            {
                return Results.NotFound();
            }

            await publisher.PublishArchiveChangedAsync(ct);
            return Results.Ok();
        });

        api.MapPost("/archive/{trackId:guid}/keep-local", async (
            Guid trackId, MetadataReviewService review,
            IProductionUpdatePublisher publisher, CancellationToken ct) =>
        {
            if (!await review.KeepLocalAsync(trackId, ct))
            {
                return Results.NotFound();
            }

            await publisher.PublishArchiveChangedAsync(ct);
            return Results.Ok();
        });

        api.MapPost("/archive/review/accept-matched", async (
            MetadataReviewService review, IProductionUpdatePublisher publisher, CancellationToken ct) =>
        {
            var promoted = await review.AcceptAllMatchedAsync(ct);
            if (promoted > 0)
            {
                await publisher.PublishArchiveChangedAsync(ct);
            }

            return Results.Ok(new { promoted });
        });

        api.MapGet("/archive/status", async (
            RadioDbContext db,
            LibraryImportService importService,
            StationSettingsCache settingsCache,
            IOptions<LibraryOptions> libraryOptions,
            CancellationToken ct) =>
        {
            var settings = await settingsCache.GetAsync(ct);
            var counts = await db.Tracks.AsNoTracking()
                .Where(t => t.Source != TrackSource.Generated)
                .GroupBy(t => new { t.Source, t.MetadataStatus, t.IsRetired, t.FileMissing })
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var scan = importService.Status;
            return Results.Ok(new ArchiveStatusDto(
                ExternalTracks: counts.Where(c => c.Key.Source == TrackSource.External).Sum(c => c.Count),
                UploadedTracks: counts.Where(c => c.Key.Source == TrackSource.Uploaded).Sum(c => c.Count),
                RetiredTracks: counts.Where(c => c.Key.IsRetired || c.Key.FileMissing).Sum(c => c.Count),
                LocalOnly: counts.Where(c => c.Key.MetadataStatus is MetadataStatus.LocalOnly or MetadataStatus.NeedsReview).Sum(c => c.Count),
                Matched: counts.Where(c => c.Key.MetadataStatus is MetadataStatus.Matched or MetadataStatus.AutoMatched).Sum(c => c.Count),
                NeedsReview: counts.Where(c => c.Key.MetadataStatus == MetadataStatus.Ambiguous).Sum(c => c.Count),
                Verified: counts.Where(c => c.Key.MetadataStatus == MetadataStatus.Verified).Sum(c => c.Count),
                LastScanUtc: scan.LastScanUtc,
                ConfiguredFolders: scan.ConfiguredFolders,
                ScanRunning: scan.ScanRunning,
                UploadEnabled: settings.ArchiveUploadEnabled,
                MaxUploadBytes: libraryOptions.Value.MaxUploadBytes));
        });

        api.MapPost("/archive/rescan", (LibraryImportService importService) =>
        {
            importService.RequestRescan();
            return Results.Accepted(value: "Library rescan requested.");
        });

        api.MapPost("/archive/upload", UploadAsync).DisableAntiforgery();

        // Uploaded tracks only: external-folder music cannot be deleted, by design.
        api.MapDelete("/archive/{id:guid}", async (
            Guid id,
            RadioDbContext db,
            QueueStateTracker queue,
            TrackDeletionService deletions,
            IProductionUpdatePublisher publisher,
            CancellationToken ct) =>
        {
            var source = await db.Tracks.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => (TrackSource?)t.Source)
                .FirstOrDefaultAsync(ct);
            if (source is null)
            {
                return Results.NotFound();
            }

            if (source != TrackSource.Uploaded)
            {
                return Results.Conflict("External library files are managed by their folder and cannot be deleted here.");
            }

            if (deletions.IsTrackActive(id))
            {
                TrackDeletionResult queued = await deletions.QueueForDeletionAsync(id, ct);
                return queued.Status == TrackDeletionStatus.NotFound
                    ? Results.NotFound()
                    : Results.Accepted(value: "Track deletion queued after playback finishes.");
            }

            if (queue.Snapshot().Any(q => q.ItemType == PlayoutItemType.Track && q.ItemId == id))
            {
                return Results.Conflict("Track is queued for playout - try again before or after it has played.");
            }

            TrackDeletionResult deleted = await deletions.DeleteNowAsync(id, ct);
            if (deleted.Status == TrackDeletionStatus.Deleted)
            {
                await publisher.PublishArchiveChangedAsync(ct);
            }

            return deleted.Status == TrackDeletionStatus.NotFound ? Results.NotFound() : Results.NoContent();
        });
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        StationSettingsCache settingsCache,
        ArchiveUploadService uploads,
        IProductionUpdatePublisher publisher,
        IOptions<LibraryOptions> libraryOptions,
        CancellationToken ct)
    {
        var settings = await settingsCache.GetAsync(ct);
        if (!settings.ArchiveUploadEnabled)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || string.IsNullOrEmpty(HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value))
        {
            return Results.BadRequest(new { error = "Expected a multipart file upload." });
        }

        var maxBytes = libraryOptions.Value.MaxUploadBytes;
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBytes + (1024 * 1024); // multipart overhead
        }

        var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!, request.Body);
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.IsFileDisposition())
            {
                continue;
            }

            var fileName = disposition.FileName.Value ?? "upload";
            var result = await uploads.StoreAsync(section.Body, fileName, ct);
            switch (result.Outcome)
            {
                case ArchiveUploadOutcome.Stored:
                    await publisher.PublishArchiveChangedAsync(ct);
                    return Results.Ok(ToArchiveDto(result.Track!, deletionPending: false));
                case ArchiveUploadOutcome.Duplicate:
                    return Results.Conflict(new
                    {
                        error = $"This audio is already in the archive as \"{result.ExistingTitle}\".",
                        trackId = result.ExistingTrackId,
                    });
                case ArchiveUploadOutcome.TooLarge:
                    return Results.BadRequest(new
                    {
                        error = $"File exceeds the upload limit of {maxBytes / (1024 * 1024)} MB.",
                    });
                default:
                    return Results.BadRequest(new { error = "The file does not look like a WAV/MP3 audio file." });
            }
        }

        return Results.BadRequest(new { error = "The upload contained no file." });
    }

    private static IReadOnlyList<string> ParseReasons(string reasonsJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(reasonsJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static ArchiveTrackDto ToArchiveDto(Track track, bool deletionPending, int candidateCount = 0) => new(
        track.Id,
        track.Title,
        track.ImportedArtist,
        track.ImportedAlbum,
        track.ImportedYear,
        track.Genre,
        track.DurationSeconds,
        track.Source.ToString(),
        track.MetadataStatus.ToString(),
        track.MetadataConfidence,
        track.PlayCount,
        track.UpVotes,
        track.DownVotes,
        IsRetired: track.IsRetired || track.FileMissing,
        track.CreatedAt,
        CandidateCount: candidateCount,
        DeletionPending: deletionPending);
}
