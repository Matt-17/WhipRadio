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
    private static void MapStats(RouteGroupBuilder api)
    {
        api.MapGet("/stats", async (RadioDbContext db, IHttpClientFactory httpFactory,
            IOptions<IcecastOptions> icecast, TimeProvider time, CancellationToken ct) =>
        {
            var listeners = 0;
            var peak = 0;
            try
            {
                var client = httpFactory.CreateClient("icecast-admin");
                var status = await client.GetFromJsonAsync<IcecastStatus>(
                    $"http://{icecast.Value.Host}:{icecast.Value.Port}/status-json.xsl", ct);
                listeners = status?.IceStats?.Source?.Listeners ?? 0;
                peak = status?.IceStats?.Source?.ListenerPeak ?? 0;
            }
            catch
            {
                // station stats still useful without icecast
            }

            var hourAgo = time.GetUtcNow().UtcDateTime.AddHours(-1);
            var topArtistsRaw = await db.Tracks.AsNoTracking()
                .Where(t => t.Artist != null)
                .GroupBy(t => t.Artist!.Name)
                .Select(g => new { Name = g.Key, Plays = g.Sum(t => t.PlayCount) })
                .OrderByDescending(x => x.Plays)
                .Take(8)
                .ToListAsync(ct);
            var topArtists = topArtistsRaw.Select(x => new NameCountDto(x.Name, x.Plays)).ToList();

            var hostAirtime = await db.PlayLog.AsNoTracking()
                .Where(e => e.ModeratorId != null)
                .GroupBy(e => e.ModeratorId!.Value)
                .Select(g => new { ModeratorId = g.Key, Seconds = g.Sum(e => e.DurationSeconds) })
                .ToListAsync(ct);
            var moderatorNames = await db.Moderators.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name, ct);

            var tracksPerGenreRaw = await db.Tracks.AsNoTracking()
                .GroupBy(t => t.Genre)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct);
            var tracksPerGenre = tracksPerGenreRaw.Select(x => new NameCountDto(x.Name, x.Count)).ToList();

            return Results.Ok(new StatsDto(
                CurrentListeners: listeners,
                ListenerPeak: peak,
                TotalTracks: await db.Tracks.CountAsync(ct),
                TotalArtists: await db.Artists.CountAsync(ct),
                TotalAnnouncements: await db.Announcements.CountAsync(ct),
                TotalPlays: await db.PlayLog.CountAsync(ct),
                PlaysLastHour: await db.PlayLog.CountAsync(e => e.PlayedAt >= hourAgo, ct),
                TotalVotes: await db.Votes.CountAsync(ct),
                TotalMusicHours: Math.Round(await db.Tracks.SumAsync(t => t.DurationSeconds * t.PlayCount, ct) / 3600, 2),
                TopArtists: topArtists,
                HostAirtimeMinutes: hostAirtime
                    .Select(h => new NameCountDto(moderatorNames.GetValueOrDefault(h.ModeratorId, "?"), Math.Round(h.Seconds / 60, 1)))
                    .OrderByDescending(x => x.Value)
                    .ToList(),
                TracksPerGenre: tracksPerGenre));
        });
    }

    private sealed record IcecastStatus([property: JsonPropertyName("icestats")] IceStats? IceStats);

    private sealed record IceStats([property: JsonPropertyName("source")] IcecastSource? Source);

    private sealed record IcecastSource(
        [property: JsonPropertyName("listeners")] int Listeners,
        [property: JsonPropertyName("listener_peak")] int ListenerPeak);
}
