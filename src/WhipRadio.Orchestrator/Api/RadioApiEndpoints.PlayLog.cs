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
    private static void MapPlayLog(RouteGroupBuilder api)
    {
        api.MapGet("/playlog", async (RadioDbContext db, CancellationToken ct) =>
        {
            var entries = await db.PlayLog.AsNoTracking()
                .OrderByDescending(e => e.PlayedAt)
                .Take(100)
                .ToListAsync(ct);

            var trackIds = entries.Where(e => e.ItemType == PlayoutItemType.Track).Select(e => e.ItemId).ToList();
            var announcementIds = entries.Where(e => e.ItemType == PlayoutItemType.Announcement).Select(e => e.ItemId).ToList();

            var tracks = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                .Where(t => trackIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);
            var announcements = await db.Announcements.AsNoTracking()
                .Where(a => announcementIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, ct);
            var talkBreaks = await db.TalkBreaks.AsNoTracking()
                .Include(talkBreak => talkBreak.Parts)
                .Where(talkBreak => talkBreak.AnnouncementId != null
                    && announcementIds.Contains(talkBreak.AnnouncementId.Value))
                .ToDictionaryAsync(talkBreak => talkBreak.AnnouncementId!.Value, ct);
            var moderators = await db.Moderators.AsNoTracking()
                .ToDictionaryAsync(m => m.Id, m => new PlayLogHostDto(m.Name, m.Slug), ct);

            // A news package stitches several segments together, each potentially
            // voiced by a different specialist host (e.g. a news host and a weather
            // host). The composite announcement only records the lead host, so pull
            // the full host roster from the package's saved segments.
            var newsHostIdsByAnnouncement = new Dictionary<Guid, List<int>>();
            var newsPackages = await db.NewsPackages.AsNoTracking()
                .Where(p => p.AnnouncementId != null
                    && announcementIds.Contains(p.AnnouncementId.Value)
                    && p.ProducedSegmentsJson != null)
                .Select(p => new { p.AnnouncementId, p.ProducedSegmentsJson })
                .ToListAsync(ct);
            foreach (var package in newsPackages)
            {
                if (package.AnnouncementId is not { } annId || package.ProducedSegmentsJson is not { } json)
                {
                    continue;
                }

                var hostIds = (JsonSerializer.Deserialize<List<NewsPackageSegmentState>>(json) ?? [])
                    .Select(s => s.SegmentHostModeratorId)
                    .Where(id => id != 0)
                    .Distinct()
                    .ToList();
                if (hostIds.Count > 0)
                {
                    newsHostIdsByAnnouncement[annId] = hostIds;
                }
            }

            var result = entries.Select(e =>
            {
                string title;
                string? transcript = null;
                string? artistName = null;
                string? artistSlug = null;
                var isNews = false;
                IReadOnlyList<PlayLogHostDto>? hosts = null;
                var isDeleted = false;
                if (e.ItemType == PlayoutItemType.Track)
                {
                    var track = tracks.GetValueOrDefault(e.ItemId);
                    isDeleted = track is null;
                    title = track?.Title ?? "deleted track";
                    artistName = track?.Artist?.Name;
                    artistSlug = track?.Artist?.Slug;
                }
                else
                {
                    var announcement = announcements.GetValueOrDefault(e.ItemId);
                    isNews = announcement?.Kind == AnnouncementKind.News;
                    title = announcement is null
                        ? "(announcement)"
                        : RadioDisplayNames.AnnouncementTitle(announcement.Kind.ToString());
                    transcript = TranscriptOf(announcement);

                    // News carries its multi-host roster; other talk has the single
                    // host who voiced it.
                    if (newsHostIdsByAnnouncement.TryGetValue(e.ItemId, out var newsHostIds))
                    {
                        hosts = newsHostIds
                            .Select(id => moderators.GetValueOrDefault(id))
                            .Where(host => host is not null)
                            .Select(host => host!)
                            .ToList();
                    }
                    else if (e.ModeratorId is int id && moderators.TryGetValue(id, out var host))
                    {
                        hosts = [host];
                    }
                }

                return new PlayLogEntryDto(
                    e.PlayedAt, e.ItemType.ToString(), e.ItemId, title,
                    hosts is { Count: > 0 } ? hosts : null,
                    e.DurationSeconds, transcript,
                    talkBreaks.TryGetValue(e.ItemId, out var talkBreak)
                        ? talkBreak.Parts
                            .OrderBy(part => part.SortOrder)
                            .Select(ToDto)
                            .ToList()
                        : null,
                    artistName,
                    artistSlug,
                    isNews,
                    isDeleted,
                    e.WasFallback);
            }).ToList();

            return Results.Ok(result);
        });

        api.MapGet("/announcements/{id:guid}/audio", async (Guid id, RadioDbContext db, IOptions<RadioOptions> radio, CancellationToken ct) =>
        {
            var announcement = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (announcement is null)
            {
                return Results.NotFound();
            }

            var path = Path.Combine(radio.Value.DataRoot, announcement.FilePath);
            return File.Exists(path)
                ? Results.File(path, "audio/wav", enableRangeProcessing: true)
                : Results.NotFound();
        });
    }
}
