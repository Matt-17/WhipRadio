using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteRetireArtistAsync(CharacterToolCall call, CancellationToken ct)
    {
        string value = Require(call, "artist");
        string reason = Require(call, "reason");

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Artist? artist = await ResolveArtistTrackedAsync(db, value, ct);
        if (artist is null)
        {
            return Failed(call, $"No artist named '{value}' was found.");
        }

        if (artist.IsRetired)
        {
            return Succeeded(call, $"{artist.Name} is already retired.");
        }

        artist.IsRetired = true;
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(new StationNotification(
            "Library",
            "chat:RetireArtist",
            $"{artist.Name} retired from future production: {reason}",
            timeProvider.GetUtcNow().UtcDateTime), ct);
        return Succeeded(call, $"{artist.Name} is retired and will no longer get new material.");
    }

    private async Task<ChatActionRecord> ExecuteDeleteArtistAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "artist");
        string reason = Require(call, "reason");

        Guid artistId;
        string artistName;
        await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            Artist? artist = await ResolveArtistTrackedAsync(db, value, ct);
            if (artist is null)
            {
                return Failed(call, $"No artist named '{value}' was found.");
            }

            artistId = artist.Id;
            artistName = artist.Name;
        }

        if (await GateAsync(call, context, ApprovalRisk.Library, $"Delete artist {artistName} ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        ArtistDeletionService deletion = scope.ServiceProvider.GetRequiredService<ArtistDeletionService>();
        ArtistDeletionResult result = await deletion.DeleteAsync(artistId, ct);
        return result.Status switch
        {
            ArtistDeletionStatus.Deleted => Succeeded(call, $"Artist {artistName} was deleted."),
            ArtistDeletionStatus.HasTracks => Failed(
                call,
                $"{artistName} still has {result.TrackCount} track(s); delete or retire those first."),
            ArtistDeletionStatus.InProduction => Failed(call, $"{artistName} is in production right now; try again later."),
            _ => Failed(call, $"{artistName} was not found."),
        };
    }

    private async Task<ChatActionRecord> ExecuteDeleteTrackAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "track");
        string reason = Require(call, "reason");

        Guid trackId;
        string trackTitle;
        await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            Track? track = await ResolveTrackTrackedAsync(db, value, ct);
            if (track is null)
            {
                return Failed(call, $"No track titled '{value}' was found.");
            }

            trackId = track.Id;
            trackTitle = track.Title;
        }

        if (await GateAsync(call, context, ApprovalRisk.Library, $"Delete track '{trackTitle}' ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        TrackDeletionService deletion = scope.ServiceProvider.GetRequiredService<TrackDeletionService>();
        TrackDeletionResult result = deletion.IsTrackActive(trackId)
            ? await deletion.QueueForDeletionAsync(trackId, ct)
            : await deletion.DeleteNowAsync(trackId, ct);
        return result.Status switch
        {
            TrackDeletionStatus.Deleted => Succeeded(call, $"Track '{trackTitle}' was deleted."),
            TrackDeletionStatus.Queued => Succeeded(call, $"'{trackTitle}' is playing now; it will be deleted after it finishes."),
            TrackDeletionStatus.AlreadyQueued => Succeeded(call, $"'{trackTitle}' is already queued for deletion."),
            _ => Failed(call, $"'{trackTitle}' was not found."),
        };
    }

    private async Task<ChatActionRecord> ExecuteRedefineArtistProfileAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "artist");
        string? hint = Optional(call, "hint");

        Guid artistId;
        string artistName;
        bool hasReleasedTracks;
        await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            Artist? artist = await ResolveArtistTrackedAsync(db, value, ct);
            if (artist is null)
            {
                return Failed(call, $"No artist named '{value}' was found.");
            }

            artistId = artist.Id;
            artistName = artist.Name;
            hasReleasedTracks = await db.Tracks.AsNoTracking()
                .AnyAsync(track => track.ArtistId == artistId && !track.IsRetired, ct);
        }

        // Rebuilding a profile that already has released tracks is authority-sensitive.
        if (hasReleasedTracks
            && await GateAsync(call, context, ApprovalRisk.Library, $"Redefine profile for {artistName}", ct)
                is { } queued)
        {
            return queued;
        }

        RedefineArtistInBackgroundAsync(artistId, artistName, hint).Forget(logger);
        return Succeeded(
            call,
            $"Rewriting {artistName}'s profile now (name and songs stay). It updates on the Artists page when ready.");
    }

    private async Task RedefineArtistInBackgroundAsync(Guid artistId, string artistName, string? hint)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            ArtistCreationService creation = scope.ServiceProvider.GetRequiredService<ArtistCreationService>();
            await creation.RedefineArtistAsync(artistId, hint, CancellationToken.None);
            await notifications.PublishAsync(new StationNotification(
                "Artist",
                "chat:RedefineArtistProfile",
                $"{artistName}'s profile was rewritten.",
                timeProvider.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested redefine of artist {ArtistId} failed", artistId);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:RedefineArtistProfile",
                $"Could not rewrite {artistName}'s profile: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteCancelSongProductionAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string reason = Require(call, "reason");

        // The director cancelling another artist's in-flight job needs confirmation
        // unless the job is already gone. Artists may cancel their own job freely.
        if (context.SenderRole == CharacterRole.ProgramDirector
            && await GateAsync(call, context, ApprovalRisk.Library, $"Cancel current song production ({reason})", ct)
                is { } queued)
        {
            return queued;
        }

        bool cancelled = musicControl.CancelGeneration();
        return cancelled
            ? Succeeded(call, "Cancelled the song currently in production.")
            : Succeeded(call, "No song is being produced right now, so there was nothing to cancel.");
    }

    private async Task<ChatActionRecord> ExecuteDeleteJingleAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string value = Require(call, "jingle");
        string reason = Require(call, "reason");

        Guid jingleId;
        string jingleLabel;
        await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            Jingle? jingle = await ResolveJingleTrackedAsync(db, value, ct);
            if (jingle is null)
            {
                return Failed(call, $"No jingle labelled '{value}' was found.");
            }

            jingleId = jingle.Id;
            jingleLabel = jingle.Label;
        }

        if (await GateAsync(call, context, ApprovalRisk.Library, $"Delete jingle '{jingleLabel}' ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext writeDb = await dbFactory.CreateDbContextAsync(ct);
        Jingle? row = await writeDb.Jingles.FirstOrDefaultAsync(j => j.Id == jingleId, ct);
        if (row is null)
        {
            return Failed(call, $"Jingle '{jingleLabel}' was already removed.");
        }

        writeDb.Jingles.Remove(row);
        await writeDb.SaveChangesAsync(ct);
        TryDeleteFile(row.FilePath);
        await hub.Clients.All.SendAsync("JinglesChanged", ct);
        return Succeeded(call, $"Jingle '{jingleLabel}' was deleted.");
    }

    private static void TryDeleteFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A locked/absent imaging file is not worth failing the delete over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<Artist?> ResolveArtistTrackedAsync(RadioDbContext db, string value, CancellationToken ct)
    {
        if (Guid.TryParse(value, out Guid id))
        {
            Artist? byId = await db.Artists.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        string lowered = value.Trim().ToLower();
        List<Artist> named = await db.Artists.Where(a => a.Name.ToLower() == lowered).Take(2).ToListAsync(ct);
        return named.Count == 1 ? named[0] : null;
    }

    private static async Task<Track?> ResolveTrackTrackedAsync(RadioDbContext db, string value, CancellationToken ct)
    {
        if (Guid.TryParse(value, out Guid id))
        {
            Track? byId = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        string lowered = value.Trim().ToLower();
        List<Track> named = await db.Tracks.Where(t => t.Title.ToLower() == lowered).Take(2).ToListAsync(ct);
        return named.Count == 1 ? named[0] : null;
    }

    private static async Task<Jingle?> ResolveJingleTrackedAsync(RadioDbContext db, string value, CancellationToken ct)
    {
        if (Guid.TryParse(value, out Guid id))
        {
            Jingle? byId = await db.Jingles.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        string lowered = value.Trim().ToLower();
        List<Jingle> named = await db.Jingles.Where(j => j.Label.ToLower() == lowered).Take(2).ToListAsync(ct);
        return named.Count == 1 ? named[0] : null;
    }
}
