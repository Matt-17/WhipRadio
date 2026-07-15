using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public class ArtistSocialFeedService(
    IDbContextFactory<RadioDbContext> dbFactory,
    MusicCopywriter copywriter,
    IArtistPostUpdatePublisher updatePublisher,
    ILogger<ArtistSocialFeedService> logger)
{
    private const int RecentPostCount = 8;
    private const int RecentSongCount = 12;

    public async Task TryCreateArtistCreatedPostAsync(Guid artistId, CancellationToken ct)
        => await TryCreatePostAsync(artistId, trackId: null, ArtistPostKind.ArtistCreated, ct);

    public async Task TryCreateTrackReleasedPostAsync(Guid artistId, Guid trackId, CancellationToken ct)
        => await TryCreatePostAsync(artistId, trackId, ArtistPostKind.TrackReleased, ct);

    public async Task<PagedArtistPostsDto> GetPostsAsync(int? page, int? pageSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pageNumber = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 50);
        var query = db.ArtistPosts.AsNoTracking();
        var total = await query.CountAsync(ct);
        var posts = await query
            .Include(post => post.Artist)
            .Include(post => post.Track)
            .OrderByDescending(post => post.CreatedAtUtc)
            .ThenByDescending(post => post.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(post => new ArtistPostDto(
                post.Id,
                post.ArtistId,
                post.Artist.Name,
                post.Artist.Slug,
                post.TrackId,
                post.Track == null ? null : post.Track.Title,
                post.Kind.ToString(),
                post.Body,
                post.CreatedAtUtc))
            .ToListAsync(ct);

        return new PagedArtistPostsDto(total, pageNumber, size, posts);
    }

    private async Task TryCreatePostAsync(Guid artistId, Guid? trackId, ArtistPostKind kind, CancellationToken ct)
    {
        try
        {
            Artist artist;
            Track? track = null;
            List<ArtistRecentPostItem> recentPosts;
            List<ArtistSongHistoryItem> songHistory;

            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                artist = await db.Artists.AsNoTracking()
                    .Include(a => a.Members)
                    .FirstOrDefaultAsync(a => a.Id == artistId, ct)
                    ?? throw new InvalidOperationException($"Artist {artistId} was not found.");

                if (trackId is { } id)
                {
                    track = await db.Tracks.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == id && t.ArtistId == artistId, ct)
                        ?? throw new InvalidOperationException($"Track {id} was not found for artist {artistId}.");
                }

                recentPosts = await LoadRecentPostsAsync(db, artistId, ct);
                songHistory = await LoadSongHistoryAsync(db, artistId, trackId, ct);
            }

            var plan = await copywriter.PlanArtistPostAsync(artist, recentPosts, kind, track, songHistory, ct);
            if (!plan.ShouldPost)
            {
                logger.LogDebug(
                    "Artist {Artist} skipped {Kind} post: {Reason}",
                    artist.Name,
                    kind,
                    plan.Text);
                return;
            }

            var body = SanitizeBody(plan.Text);
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            ArtistPostDto? dto;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var post = new ArtistPost
                {
                    Id = Guid.NewGuid(),
                    ArtistId = artistId,
                    TrackId = trackId,
                    Kind = kind,
                    Body = body,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                db.ArtistPosts.Add(post);
                await db.SaveChangesAsync(ct);
                dto = new ArtistPostDto(
                    post.Id,
                    artist.Id,
                    artist.Name,
                    artist.Slug,
                    track?.Id,
                    track?.Title,
                    post.Kind.ToString(),
                    post.Body,
                    post.CreatedAtUtc);
            }

            await updatePublisher.PublishPostAddedAsync(dto, ct);
            logger.LogInformation("Posted artist wire update for {Artist}: {Kind}", artist.Name, kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Artist social post generation failed for artist {ArtistId}, kind {Kind}", artistId, kind);
        }
    }

    private static async Task<List<ArtistRecentPostItem>> LoadRecentPostsAsync(
        RadioDbContext db,
        Guid artistId,
        CancellationToken ct)
        => await db.ArtistPosts.AsNoTracking()
            .Include(post => post.Track)
            .Where(post => post.ArtistId == artistId)
            .OrderByDescending(post => post.CreatedAtUtc)
            .Take(RecentPostCount)
            .Select(post => new ArtistRecentPostItem(
                post.Kind.ToString(),
                post.Body,
                post.CreatedAtUtc,
                post.Track == null ? null : post.Track.Title))
            .ToListAsync(ct);

    private static async Task<List<ArtistSongHistoryItem>> LoadSongHistoryAsync(
        RadioDbContext db,
        Guid artistId,
        Guid? excludeTrackId,
        CancellationToken ct)
        => await db.Tracks.AsNoTracking()
            .Where(track => track.ArtistId == artistId && (excludeTrackId == null || track.Id != excludeTrackId))
            .OrderByDescending(track => track.CreatedAt)
            .Take(RecentSongCount)
            .Select(track => new ArtistSongHistoryItem(
                track.Title,
                track.Style,
                track.Language,
                track.HasVocals,
                track.SongStory,
                track.TargetDurationSeconds,
                track.DurationSeconds,
                track.UpVotes,
                track.DownVotes))
            .ToListAsync(ct);

    /// <summary>
    /// Publishes a feed post the artist wrote themselves (via chat). The body is
    /// sanitized and length-limited, but not routed through the copywriter.
    /// </summary>
    public async Task CreateAgentPostAsync(Guid artistId, string body, Guid? trackId, CancellationToken ct)
    {
        string sanitized = SanitizeBody(body);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException("The post body is empty.");
        }

        if (sanitized.Length > 500)
        {
            sanitized = sanitized[..500];
        }

        ArtistPostDto dto;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            Artist artist = await db.Artists.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artistId, ct)
                ?? throw new InvalidOperationException($"Artist {artistId} was not found.");

            Track? track = null;
            if (trackId is { } id)
            {
                track = await db.Tracks.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == id && t.ArtistId == artistId, ct)
                    ?? throw new InvalidOperationException($"Track {id} was not found for this artist.");
            }

            var post = new ArtistPost
            {
                Id = Guid.NewGuid(),
                ArtistId = artistId,
                TrackId = trackId,
                Kind = ArtistPostKind.StatusUpdate,
                Body = sanitized,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.ArtistPosts.Add(post);
            await db.SaveChangesAsync(ct);
            dto = new ArtistPostDto(
                post.Id,
                artist.Id,
                artist.Name,
                artist.Slug,
                track?.Id,
                track?.Title,
                post.Kind.ToString(),
                post.Body,
                post.CreatedAtUtc);
        }

        await updatePublisher.PublishPostAddedAsync(dto, ct);
        logger.LogInformation("Artist {ArtistId} posted a chat-written feed update.", artistId);
    }

    private static string SanitizeBody(string value)
    {
        var sanitized = LlmOutputSanitizer.Sanitize(value)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim()
            .Trim('"');
        sanitized = string.Join(' ', sanitized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return sanitized;
    }
}
