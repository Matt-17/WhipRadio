using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Api;

public static class RadioApiEndpoints
{
    public static IEndpointRouteBuilder MapRadioApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/nowplaying", (INowPlayingState nowPlaying) =>
        {
            var current = nowPlaying.Current;
            return current is null
                ? Results.NoContent()
                : Results.Ok(new NowPlayingDto(
                    current.ItemType.ToString(),
                    current.ItemId,
                    current.Title,
                    current.StartedAtUtc,
                    current.DurationSeconds,
                    current.ModeratorName));
        });

        api.MapGet("/library", async (RadioDbContext db, string? sort, CancellationToken ct) =>
        {
            var query = db.Tracks.AsNoTracking();
            query = sort?.ToLowerInvariant() switch
            {
                "plays" => query.OrderByDescending(t => t.PlayCount),
                "votes" => query.OrderByDescending(t => t.UpVotes - t.DownVotes),
                _ => query.OrderByDescending(t => t.CreatedAt),
            };

            var tracks = await query.Take(500).ToListAsync(ct);
            return Results.Ok(tracks.Select(ToDto).ToList());
        });

        api.MapGet("/playlog", async (RadioDbContext db, CancellationToken ct) =>
        {
            var entries = await db.PlayLog.AsNoTracking()
                .OrderByDescending(e => e.PlayedAt)
                .Take(100)
                .ToListAsync(ct);

            var trackIds = entries.Where(e => e.ItemType == PlayoutItemType.Track).Select(e => e.ItemId).ToList();
            var announcementIds = entries.Where(e => e.ItemType == PlayoutItemType.Announcement).Select(e => e.ItemId).ToList();

            var trackTitles = await db.Tracks.AsNoTracking()
                .Where(t => trackIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Title, ct);
            var announcementKinds = await db.Announcements.AsNoTracking()
                .Where(a => announcementIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Kind.ToString(), ct);
            var moderatorNames = await db.Moderators.AsNoTracking()
                .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

            var result = entries.Select(e => new PlayLogEntryDto(
                e.PlayedAt,
                e.ItemType.ToString(),
                e.ItemType == PlayoutItemType.Track
                    ? trackTitles.GetValueOrDefault(e.ItemId, "(deleted track)")
                    : announcementKinds.GetValueOrDefault(e.ItemId, "(announcement)"),
                e.ModeratorId is int id ? moderatorNames.GetValueOrDefault(id) : null)).ToList();

            return Results.Ok(result);
        });

        api.MapPost("/votes", async (VoteRequestDto request, HttpContext http, RadioDbContext db, CancellationToken ct) =>
        {
            if (request.Direction is not (1 or -1))
            {
                return Results.BadRequest("Direction must be +1 or -1.");
            }

            var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == request.TrackId, ct);
            if (track is null)
            {
                return Results.NotFound();
            }

            if (request.Direction > 0)
            {
                track.UpVotes++;
            }
            else
            {
                track.DownVotes++;
            }

            track.IsRetired = track.IsRetired || TrackWeighting.ShouldRetire(track);

            db.Votes.Add(new Vote
            {
                TrackId = track.Id,
                Direction = request.Direction,
                CreatedAt = DateTime.UtcNow,
                ClientHint = HashClient(http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok(new VoteResultDto(track.Id, track.UpVotes, track.DownVotes, track.IsRetired));
        });

        api.MapGet("/moderators", async (RadioDbContext db, CancellationToken ct) =>
        {
            var moderators = await db.Moderators.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
            return Results.Ok(moderators.Select(m => new ModeratorDto(
                m.Id, m.Name, m.Language, m.VoiceId, m.SpeechRate, m.Style,
                m.PersonaPrompt, m.PrefersVocals, m.PreferredGenres, m.IsActive)).ToList());
        });

        api.MapPost("/moderators/{id:int}/toggle", async (int id, RadioDbContext db, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.IsActive = !moderator.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { moderator.Id, moderator.IsActive });
        });

        api.MapGet("/settings", async (RadioDbContext db, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
            return Results.Ok(new StationSettingsDto(
                settings.StationName, settings.DefaultLanguage, settings.TargetQueueLength, settings.AnnouncementEveryNTracks));
        });

        api.MapPut("/settings", async (StationSettingsDto request, RadioDbContext db, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FirstOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            settings.StationName = string.IsNullOrWhiteSpace(request.StationName) ? settings.StationName : request.StationName.Trim();
            settings.DefaultLanguage = string.IsNullOrWhiteSpace(request.DefaultLanguage) ? settings.DefaultLanguage : request.DefaultLanguage.Trim();
            settings.TargetQueueLength = Math.Clamp(request.TargetQueueLength, 1, 20);
            settings.AnnouncementEveryNTracks = Math.Clamp(request.AnnouncementEveryNTracks, 0, 10);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new StationSettingsDto(
                settings.StationName, settings.DefaultLanguage, settings.TargetQueueLength, settings.AnnouncementEveryNTracks));
        });

        return app;
    }

    private static TrackDto ToDto(Track t) => new(
        t.Id, t.Title, t.Genre, t.HasVocals, t.DurationSeconds,
        t.PlayCount, t.UpVotes, t.DownVotes, t.IsRetired, t.Backend, t.CreatedAt);

    private static string HashClient(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
