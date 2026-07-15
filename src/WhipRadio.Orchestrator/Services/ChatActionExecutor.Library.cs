using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteSearchArtistAsync(CharacterToolCall call, CancellationToken ct)
    {
        string style = Require(call, "style");
        string? genre = Optional(call, "genre");
        string? subgenre = Optional(call, "subgenre");
        bool createIfMissing = !string.Equals(Optional(call, "createIfMissing"), "false", StringComparison.OrdinalIgnoreCase);
        int limit = int.TryParse(Optional(call, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 1, 10)
            : 5;

        string[] tokens = style.ToLowerInvariant()
            .Split([' ', ',', ';', '-', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Artists.AsNoTracking()
            .Where(artist => !artist.IsRetired)
            .Select(artist => new
            {
                artist.Name,
                artist.Genre,
                artist.Subgenre,
                artist.StyleDescriptor,
                artist.Type,
            })
            .ToListAsync(ct);

        var matches = candidates
            .Select(artist => new
            {
                Artist = artist,
                Score = Score(artist.Name, artist.Genre, artist.Subgenre, artist.StyleDescriptor, tokens, genre, subgenre),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Artist.Name)
            .Take(limit)
            .ToList();

        if (matches.Count > 0)
        {
            string summary = string.Join("; ", matches.Select(item =>
                $"{item.Artist.Name} ({item.Artist.Genre}/{item.Artist.Subgenre}, {item.Artist.Type})"));
            return Succeeded(call, $"{matches.Count} matching artist(s): {summary}");
        }

        if (!createIfMissing)
        {
            return Succeeded(call, $"No active artist matches \"{style}\" and creation was declined.");
        }

        CreateArtistInBackgroundAsync(style, genre, subgenre).Forget();
        return Succeeded(
            call,
            $"No existing artist fits \"{style}\"; a new one is being written now. "
            + "It takes a while and will show up on the Artists page - don't request it again.");
    }

    private static int Score(
        string name,
        string genre,
        string subgenre,
        string style,
        string[] tokens,
        string? preferredGenre,
        string? preferredSubgenre)
    {
        string haystack = $"{name} {genre} {subgenre} {style}".ToLowerInvariant();
        int score = tokens.Count(token => haystack.Contains(token));
        if (preferredGenre is not null && genre.Contains(preferredGenre, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        if (preferredSubgenre is not null && subgenre.Contains(preferredSubgenre, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    private async Task CreateArtistInBackgroundAsync(string style, string? genre, string? subgenre)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            ArtistCreationService creation = scope.ServiceProvider.GetRequiredService<ArtistCreationService>();
            Artist artist = await creation.CreateArtistAsync(style, genre, subgenre, CancellationToken.None);
            await notifications.PublishAsync(new StationNotification(
                "Artist",
                "chat:SearchArtist",
                $"New artist {artist.Name} was written for \"{style}\".",
                timeProvider.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested artist creation for '{Style}' failed", style);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:SearchArtist",
                $"Could not write a new artist for \"{style}\": {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteGetArtistProfileAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "artist");
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Artist? artist = null;
        if (Guid.TryParse(value, out Guid id))
        {
            artist = await db.Artists.AsNoTracking().Include(a => a.Members)
                .FirstOrDefaultAsync(a => a.Id == id, ct);
        }

        artist ??= await db.Artists.AsNoTracking().Include(a => a.Members)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == value.Trim().ToLower(), ct);

        if (artist is null)
        {
            return Failed(call, $"No artist named '{value}' was found.");
        }

        // Hidden background is exposed only to the director or the artist itself.
        bool ownProfile = context.SenderRole == CharacterRole.Artist
            && context.Sender.Ref.EntityId is { } memberId
            && artist.Members.Any(member => member.Id == memberId);
        bool includeDeep = context.SenderRole == CharacterRole.ProgramDirector || ownProfile;

        List<string> recentTracks = await db.Tracks.AsNoTracking()
            .Where(track => track.ArtistId == artist.Id && !track.IsRetired)
            .OrderByDescending(track => track.CreatedAt)
            .Take(5)
            .Select(track => track.Title)
            .ToListAsync(ct);

        string members = artist.Members.Count == 0
            ? "solo/unknown"
            : string.Join(", ", artist.Members.OrderBy(m => m.SortOrder).Select(m => $"{m.Name} ({m.Role})"));
        string biography = includeDeep && !string.IsNullOrWhiteSpace(artist.DeepBackgroundBiography)
            ? artist.DeepBackgroundBiography!
            : artist.Biography ?? "no biography yet";
        string songs = recentTracks.Count == 0 ? "no released songs yet" : string.Join(", ", recentTracks);

        return Succeeded(
            call,
            $"{artist.Name} - {artist.Genre}/{artist.Subgenre}, {artist.Type}. Members: {members}. "
            + $"Recent songs: {songs}. Background: {biography}");
    }

    private async Task<ChatActionRecord> ExecuteQueueTrackAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "track");
        bool jumpLine = string.Equals(Optional(call, "priority"), "next", StringComparison.OrdinalIgnoreCase);

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Track track;
        if (Guid.TryParse(value, out Guid id))
        {
            track = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                .FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new InvalidOperationException($"Track '{value}' was not found.");
        }
        else
        {
            string lowered = value.Trim().ToLower();
            List<Track> named = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                .Where(t => t.Title.ToLower() == lowered)
                .Take(2)
                .ToListAsync(ct);
            if (named.Count == 0)
            {
                return Failed(call, $"No track titled '{value}' was found. Search first and pass the id.");
            }

            if (named.Count > 1)
            {
                return Failed(call, $"Several tracks are titled '{value}'. Use SearchMusic and pass the exact id.");
            }

            track = named[0];
        }

        if (track.IsRetired)
        {
            return Failed(call, $"'{track.Title}' is retired and cannot be queued.");
        }

        // A host may only queue during their own show; the director queues anytime.
        if (context.SenderModerator is { } host)
        {
            ShowContext show = await schedule.GetCurrentAsync(ct);
            if (show.Moderator.Id != host.Id)
            {
                return Failed(
                    call,
                    $"You are not on air right now - {show.Moderator.Name} is. Ask the Program Director to queue it.");
            }

            jumpLine = false; // only the director may jump the line
        }

        PlayoutItem item = new(
            PlayoutItemType.Track,
            track.Id,
            track.FilePath,
            $"{track.Artist?.Name ?? "Unknown"} - {track.Title}",
            track.DurationSeconds);
        if (jumpLine)
        {
            playoutQueue.EnqueueFront(item);
        }
        else
        {
            playoutQueue.Enqueue(item);
        }

        return Succeeded(
            call,
            jumpLine
                ? $"Queued '{track.Title}' to play next."
                : $"Queued '{track.Title}' for playout.");
    }

    private async Task<ChatActionRecord> ExecuteRetireTrackAsync(CharacterToolCall call, CancellationToken ct)
    {
        string value = Require(call, "track");
        string reason = Require(call, "reason");

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Track? track = null;
        if (Guid.TryParse(value, out Guid id))
        {
            track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
        }

        if (track is null)
        {
            string lowered = value.Trim().ToLower();
            List<Track> named = await db.Tracks.Where(t => t.Title.ToLower() == lowered).Take(2).ToListAsync(ct);
            if (named.Count == 0)
            {
                return Failed(call, $"No track titled '{value}' was found.");
            }

            if (named.Count > 1)
            {
                return Failed(call, $"Several tracks are titled '{value}'. Pass the exact id.");
            }

            track = named[0];
        }

        if (track.IsRetired)
        {
            return Succeeded(call, $"'{track.Title}' is already retired.");
        }

        track.IsRetired = true;
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(new StationNotification(
            "Library",
            "chat:RetireTrack",
            $"'{track.Title}' left rotation: {reason}",
            timeProvider.GetUtcNow().UtcDateTime), ct);
        return Succeeded(call, $"'{track.Title}' will no longer be selected for rotation.");
    }
}
