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
    private static void MapNowPlaying(RouteGroupBuilder api)
    {
        api.MapGet("/nowplaying", async (INowPlayingState nowPlaying, RadioDbContext db, ScheduleService schedule, CancellationToken ct) =>
        {
            var current = nowPlaying.Current;
            if (current is null)
            {
                return Results.NoContent();
            }

            string? artistName = null;
            string? transcript = null;
            string? lyrics = null;
            string? announcementKind = null;
            var title = current.Title;
            var upVotes = 0;
            var downVotes = 0;

            if (current.ItemType == PlayoutItemType.Track)
            {
                var track = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                    .FirstOrDefaultAsync(t => t.Id == current.ItemId, ct);
                artistName = track?.Artist?.Name;
                upVotes = track?.UpVotes ?? 0;
                downVotes = track?.DownVotes ?? 0;
                lyrics = track?.Lyrics;
            }
            else
            {
                var announcement = await db.Announcements.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == current.ItemId, ct);
                transcript = TranscriptOf(announcement);
                announcementKind = announcement?.Kind.ToString();
                title = RadioDisplayNames.AnnouncementTitle(announcementKind);
            }

            string? formatName = null;
            try
            {
                formatName = (await schedule.GetCurrentAsync(ct)).Format?.Name;
            }
            catch
            {
                // decoration only
            }

            return Results.Ok(new NowPlayingDto(
                current.ItemType.ToString(), current.ItemId, title, current.StartedAtUtc,
                current.DurationSeconds, current.ModeratorName, artistName, transcript, upVotes, downVotes,
                formatName, lyrics, announcementKind));
        });

        api.MapGet("/queue", (QueueStateTracker tracker) =>
            Results.Ok(tracker.Snapshot()
                .Select(q => new QueueItemDto(q.ItemType.ToString(), q.ItemId, q.Title, q.DurationSeconds))
                .ToList()));
    }

    private static void MapStationStatus(RouteGroupBuilder api)
    {
        // Snapshot of the encoder/stream health the On Air lamp reflects. The live
        // value is pushed over SignalR ("StationStatusChanged"); this endpoint
        // backs the initial HTTP snapshot a page loads on connect.
        api.MapGet("/station/status", (IStationStatusReporter reporter) =>
        {
            var info = reporter.Current;
            return Results.Ok(new StationStatusDto(info.Status.ToString(), info.Reason, info.NextAttemptUtc, info.PlayoutEnabled));
        });
    }
}
