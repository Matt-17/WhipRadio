using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.News;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Slugs;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private static void MapArtistPosts(RouteGroupBuilder api)
    {
        api.MapGet("/artist-posts", async (
            ArtistSocialFeedService feed,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            var posts = await feed.GetPostsAsync(page, pageSize, ct);
            return Results.Ok(posts);
        });
    }

    private static void MapLibrary(RouteGroupBuilder api)
    {
        api.MapGet("/library", async (
            RadioDbContext db,
            TrackDeletionService deletions,
            string? sort,
            string? genre,
            Guid? artistId,
            CancellationToken ct) =>
        {
            var query = db.Tracks.AsNoTracking().Include(t => t.Artist).AsQueryable();

            if (!string.IsNullOrEmpty(genre))
            {
                query = query.Where(t => t.Genre == genre || t.Subgenre == genre);
            }

            if (artistId is not null)
            {
                query = query.Where(t => t.ArtistId == artistId);
            }

            query = sort?.ToLowerInvariant() switch
            {
                "plays" => query.OrderByDescending(t => t.PlayCount),
                "votes" => query.OrderByDescending(t => t.UpVotes - t.DownVotes),
                "title" => query.OrderBy(t => t.Title),
                "artist" => query.OrderBy(t => t.Artist!.Name).ThenBy(t => t.Title),
                _ => query.OrderByDescending(t => t.CreatedAt),
            };

            var tracks = await query.Take(500).ToListAsync(ct);
            return Results.Ok(tracks.Select(track => ToDto(track, deletions.IsPending(track.Id))).ToList());
        });

        api.MapGet("/artists", async (RadioDbContext db, CancellationToken ct) =>
        {
            var artists = await db.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
            var aggregates = await db.Tracks.AsNoTracking()
                .Where(t => t.ArtistId != null)
                .GroupBy(t => t.ArtistId!.Value)
                .Select(g => new { ArtistId = g.Key, Count = g.Count(), Up = g.Sum(t => t.UpVotes), Down = g.Sum(t => t.DownVotes) })
                .ToDictionaryAsync(x => x.ArtistId, ct);

            return Results.Ok(artists.Select(a =>
            {
                var agg = aggregates.GetValueOrDefault(a.Id);
                return new ArtistDto(a.Id, a.Name, a.Slug, a.Genre, a.Subgenre, a.StyleDescriptor,
                    agg?.Count ?? 0, agg?.Up ?? 0, agg?.Down ?? 0, a.IsRetired, a.Biography,
                    a.Type, a.Origin, a.FormationYear, a.PromotionText, Language: a.Language);
            }).ToList());
        });

        api.MapPost("/artists", async (
            CreateArtistRequestDto request,
            ArtistCreationService artistCreator,
            RadioDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Hint))
            {
                return Results.BadRequest("Hint is required.");
            }

            Artist artist;
            try
            {
                artist = await artistCreator.CreateArtistAsync(request.Hint, ct: ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Artist creation failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var members = await db.ArtistMembers.AsNoTracking()
                .Where(m => m.ArtistId == artist.Id)
                .OrderBy(m => m.SortOrder)
                .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography, !string.IsNullOrEmpty(m.VoiceReferencePath), m.VoiceDesignLastError, m.Gender, m.Age, m.Interests, m.Personality))
                .ToListAsync(ct);

            return Results.Ok(new ArtistDto(
                artist.Id, artist.Name, artist.Slug, artist.Genre, artist.Subgenre, artist.StyleDescriptor,
                0, 0, 0, artist.IsRetired, artist.Biography,
                artist.Type, artist.Origin, artist.FormationYear, artist.PromotionText, members, artist.Language,
                artist.DeepBackgroundBiography));
        });

        api.MapPost("/artists/{id:guid}/redefine", async (
            Guid id,
            RedefineArtistRequestDto request,
            ArtistCreationService artistCreator,
            RadioDbContext db,
            CancellationToken ct) =>
        {
            Artist artist;
            try
            {
                artist = await artistCreator.RedefineArtistAsync(id, request.Hint, ct);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Artist redefinition failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var stats = await db.Tracks.AsNoTracking()
                .Where(t => t.ArtistId == id)
                .GroupBy(t => t.ArtistId)
                .Select(g => new { Count = g.Count(), Up = g.Sum(t => t.UpVotes), Down = g.Sum(t => t.DownVotes) })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(ToArtistDto(
                artist,
                stats?.Count ?? 0,
                stats?.Up ?? 0,
                stats?.Down ?? 0,
                artist.Members
                    .OrderBy(m => m.SortOrder)
                    .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography, !string.IsNullOrEmpty(m.VoiceReferencePath), m.VoiceDesignLastError, m.Gender, m.Age, m.Interests, m.Personality))
                    .ToList()));
        });

        // Artist detail; writes the biography on first view for artists that
        // predate biographies (LLM call — can take a moment).
        api.MapGet("/artists/{id:guid}", async (
            Guid id, RadioDbContext db, MusicCopywriter copywriter, CancellationToken ct) =>
        {
            var artist = await db.Artists.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (artist is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(artist.Biography))
            {
                try
                {
                    artist.Biography = await copywriter.WriteArtistBiographyAsync(artist, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch
                {
                    // Bio is decoration — never fail the detail view over it.
                }
            }

            var stats = await db.Tracks.AsNoTracking()
                .Where(t => t.ArtistId == id)
                .GroupBy(t => t.ArtistId)
                .Select(g => new { Count = g.Count(), Up = g.Sum(t => t.UpVotes), Down = g.Sum(t => t.DownVotes) })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new ArtistDto(artist.Id, artist.Name, artist.Slug, artist.Genre, artist.Subgenre,
                artist.StyleDescriptor, stats?.Count ?? 0, stats?.Up ?? 0, stats?.Down ?? 0,
                artist.IsRetired, artist.Biography,
                artist.Type, artist.Origin, artist.FormationYear, artist.PromotionText,
                artist.Members
                    .OrderBy(m => m.SortOrder)
                    .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography, !string.IsNullOrEmpty(m.VoiceReferencePath), m.VoiceDesignLastError, m.Gender, m.Age, m.Interests, m.Personality))
                    .ToList(),
                artist.Language,
                artist.DeepBackgroundBiography));
        });

        // "Create new song" — queued for the production loop, generated in the
        // artist's signature style. 202: poll /music/status for progress.
        api.MapDelete("/artists/{id:guid}", async (
            Guid id, ArtistDeletionService artists, CancellationToken ct) =>
        {
            ArtistDeletionResult result = await artists.DeleteAsync(id, ct);
            return result.Status switch
            {
                ArtistDeletionStatus.Deleted => Results.NoContent(),
                ArtistDeletionStatus.NotFound => Results.NotFound(),
                ArtistDeletionStatus.InProduction => Results.Conflict(
                    "Artist has a song queued or recording - wait until production is idle."),
                ArtistDeletionStatus.HasTracks => Results.Conflict(
                    $"Artist still has {result.TrackCount} song{(result.TrackCount == 1 ? "" : "s")}. Delete the songs first."),
                _ => Results.Problem("Artist deletion failed."),
            };
        });

        api.MapPost("/artists/{id:guid}/produce", async (
            Guid id, RadioDbContext db, MusicProductionControl control, CancellationToken ct) =>
        {
            var exists = await db.Artists.AnyAsync(a => a.Id == id && !a.IsRetired, ct);
            if (!exists)
            {
                return Results.NotFound();
            }

            control.RequestTrackFor(id);
            return Results.Accepted();
        });

        api.MapGet("/music/status", (MusicProductionControl control) =>
        {
            var current = control.Current;
            return Results.Ok(new MusicProductionStatusDto(
                current?.ArtistId, current?.ArtistName, current?.TrackTitle,
                current?.StartedAtUtc, control.QueuedArtistIds()));
        });

        api.MapPost("/music/cancel", (MusicProductionControl control) =>
            control.CancelGeneration() ? Results.Accepted() : Results.NoContent());

        // In-library preview playback — intentionally does NOT touch PlayCount;
        // only broadcast plays count.
        api.MapGet("/library/{id:guid}", async (
            Guid id, RadioDbContext db, TrackDeletionService deletions, CancellationToken ct) =>
        {
            var track = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            return track is null
                ? Results.NotFound()
                : Results.Ok(ToDto(track, deletions.IsPending(track.Id)));
        });

        api.MapGet("/library/{id:guid}/audio", async (
            Guid id, RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (track is null)
            {
                return Results.NotFound();
            }

            var absolutePath = MediaPaths.ResolveAbsolute(radioOptions.Value.DataRoot, track.FilePath);
            if (!System.IO.File.Exists(absolutePath))
            {
                return Results.NotFound();
            }

            var contentType = absolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? "audio/mpeg"
                : "audio/wav";
            return Results.File(absolutePath, contentType, enableRangeProcessing: true);
        });

        // Plays a band member's voice reference clip in the footer preview player.
        api.MapGet("/artist-members/{id:guid}/voice", async (
            Guid id, RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var member = await db.ArtistMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (member is null || string.IsNullOrEmpty(member.VoiceReferencePath))
            {
                return Results.NotFound();
            }

            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, member.VoiceReferencePath);
            if (!System.IO.File.Exists(absolutePath))
            {
                return Results.NotFound();
            }

            return Results.File(absolutePath, "audio/wav", enableRangeProcessing: true);
        });

        // (Re)designs a band member's hidden voice reference on demand. Clears the
        // stored clip — so the play button hides until the booth produces a fresh
        // one — and jumps the member to the front of the voice-design queue.
        api.MapPost("/artist-members/{id:guid}/voice/recreate", async (
            Guid id, RadioDbContext db, ArtistMemberVoiceQueue voiceQueue, CancellationToken ct) =>
        {
            var exists = await db.ArtistMembers.AsNoTracking().AnyAsync(m => m.Id == id, ct);
            if (!exists)
            {
                return Results.NotFound();
            }

            await db.ArtistMembers
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.VoiceId, (string?)null)
                    .SetProperty(m => m.VoiceReferencePath, (string?)null)
                    .SetProperty(m => m.VoiceDesignedAtUtc, (DateTime?)null)
                    .SetProperty(m => m.VoiceDesignLastError, (string?)null), ct);

            voiceQueue.EnqueuePriority(id);
            return Results.Accepted();
        });

        api.MapDelete("/library/{id:guid}", async (
            Guid id, QueueStateTracker queue, TrackDeletionService deletions, CancellationToken ct) =>
        {
            if (deletions.IsTrackActive(id))
            {
                TrackDeletionResult result = await deletions.QueueForDeletionAsync(id, ct);
                return result.Status == TrackDeletionStatus.NotFound
                    ? Results.NotFound()
                    : Results.Accepted(value: "Track deletion queued after playback finishes.");
            }

            var queuedForPlayout = queue.Snapshot().Any(q => q.ItemType == PlayoutItemType.Track && q.ItemId == id);
            if (queuedForPlayout)
            {
                return Results.Conflict("Track is queued for playout - try again before or after it has played.");
            }

            TrackDeletionResult deleted = await deletions.DeleteNowAsync(id, ct);
            return deleted.Status == TrackDeletionStatus.NotFound ? Results.NotFound() : Results.NoContent();

        });
    }
}
