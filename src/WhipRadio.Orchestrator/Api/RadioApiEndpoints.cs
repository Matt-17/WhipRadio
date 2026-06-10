using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static class RadioApiEndpoints
{
    public static IEndpointRouteBuilder MapRadioApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        MapNowPlaying(api);
        MapLibrary(api);
        MapPlayLog(api);
        MapVotes(api);
        MapModerators(api);
        MapSettings(api);
        MapFormatsAndSchedule(api);
        MapStats(api);
        MapConsole(api);

        return app;
    }

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
            var upVotes = 0;
            var downVotes = 0;

            if (current.ItemType == PlayoutItemType.Track)
            {
                var track = await db.Tracks.AsNoTracking().Include(t => t.Artist)
                    .FirstOrDefaultAsync(t => t.Id == current.ItemId, ct);
                artistName = track?.Artist?.Name;
                upVotes = track?.UpVotes ?? 0;
                downVotes = track?.DownVotes ?? 0;
            }
            else
            {
                var voicedText = (await db.Announcements.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == current.ItemId, ct))?.VoicedText;
                transcript = voicedText is null ? null : SpeechMarkerNormalizer.ToPlainText(voicedText);
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
                current.ItemType.ToString(), current.ItemId, current.Title, current.StartedAtUtc,
                current.DurationSeconds, current.ModeratorName, artistName, transcript, upVotes, downVotes, formatName));
        });

        api.MapGet("/queue", (QueueStateTracker tracker) =>
            Results.Ok(tracker.Snapshot()
                .Select(q => new QueueItemDto(q.ItemType.ToString(), q.ItemId, q.Title, q.DurationSeconds))
                .ToList()));
    }

    private static void MapLibrary(RouteGroupBuilder api)
    {
        api.MapGet("/library", async (RadioDbContext db, string? sort, string? genre, Guid? artistId, CancellationToken ct) =>
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
            return Results.Ok(tracks.Select(ToDto).ToList());
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
                return new ArtistDto(a.Id, a.Name, a.Genre, a.Subgenre, a.StyleDescriptor,
                    agg?.Count ?? 0, agg?.Up ?? 0, agg?.Down ?? 0, a.IsRetired);
            }).ToList());
        });
    }

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
            var moderatorNames = await db.Moderators.AsNoTracking()
                .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

            var result = entries.Select(e =>
            {
                string title;
                string? transcript = null;
                if (e.ItemType == PlayoutItemType.Track)
                {
                    var track = tracks.GetValueOrDefault(e.ItemId);
                    title = track is null ? "(deleted track)" : $"{track.Artist?.Name ?? "?"} — {track.Title}";
                }
                else
                {
                    var announcement = announcements.GetValueOrDefault(e.ItemId);
                    title = announcement?.Kind.ToString() ?? "(announcement)";
                    transcript = announcement?.VoicedText is { } voiced
                        ? SpeechMarkerNormalizer.ToPlainText(voiced)
                        : null;
                }

                return new PlayLogEntryDto(
                    e.PlayedAt, e.ItemType.ToString(), e.ItemId, title,
                    e.ModeratorId is int id ? moderatorNames.GetValueOrDefault(id) : null,
                    e.DurationSeconds, transcript);
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

    private static void MapVotes(RouteGroupBuilder api)
    {
        api.MapPost("/votes", async (VoteRequestDto request, HttpContext http, RadioDbContext db,
            IHubContext<RadioHub> hub, CancellationToken ct) =>
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

            var result = new VoteResultDto(track.Id, track.UpVotes, track.DownVotes, track.IsRetired);
            await hub.Clients.All.SendAsync("VotesChanged", result, ct);
            return Results.Ok(result);
        });
    }

    private static void MapModerators(RouteGroupBuilder api)
    {
        api.MapGet("/moderators", async (RadioDbContext db, CancellationToken ct) =>
        {
            var moderators = await db.Moderators.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
            return Results.Ok(moderators.Select(ToDto).ToList());
        });

        api.MapPost("/moderators", async (CreateModeratorDto request, RadioDbContext db,
            VoiceCatalogService voices, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            // Hosts always speak the station language (the main language).
            var stationLanguage = StationLanguages.Normalize(
                (await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.DefaultLanguage);

            var moderator = new Moderator
            {
                Name = request.Name.Trim(),
                Language = stationLanguage,
                Gender = request.Gender == ModeratorGenders.Male ? ModeratorGenders.Male : ModeratorGenders.Female,
                TtsEngine = string.IsNullOrWhiteSpace(request.TtsEngine) ? TtsEngines.Kokoro : request.TtsEngine,
                Style = request.Style,
                PersonaPrompt = request.PersonaPrompt,
                PrefersVocals = request.PrefersVocals,
                PreferredGenres = request.PreferredGenres,
                Talkativeness = Math.Clamp(request.Talkativeness, 0, 1),
                IsActive = true,
                SpeechRate = 1.0,
            };
            moderator.VoiceId = await voices.PickVoiceAsync(moderator, ct);

            db.Moderators.Add(moderator);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(moderator));
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

        api.MapGet("/moderators/{id:int}/talks", async (int id, RadioDbContext db, CancellationToken ct) =>
        {
            var talks = await db.Announcements.AsNoTracking()
                .Where(a => a.ModeratorId == id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync(ct);

            return Results.Ok(talks.Select(a => new PlayLogEntryDto(
                a.CreatedAt, "Announcement", a.Id, a.Kind.ToString(), null, a.DurationSeconds,
                SpeechMarkerNormalizer.ToPlainText(a.VoicedText))).ToList());
        });
    }

    private static void MapSettings(RouteGroupBuilder api)
    {
        api.MapGet("/settings", async (RadioDbContext db, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
            return Results.Ok(ToDto(settings));
        });

        api.MapPut("/settings", async (StationSettingsDto request, RadioDbContext db,
            HostLanguageAligner aligner, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FirstOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            var previousLanguage = settings.DefaultLanguage;
            settings.StationName = string.IsNullOrWhiteSpace(request.StationName) ? settings.StationName : request.StationName.Trim();
            settings.DefaultLanguage = StationLanguages.Normalize(request.DefaultLanguage);
            settings.TargetQueueLength = Math.Clamp(request.TargetQueueLength, 1, 20);
            settings.AnnouncementEveryNTracks = Math.Clamp(request.AnnouncementEveryNTracks, 0, 10);
            settings.MusicProductionEnabled = request.MusicProductionEnabled;
            settings.PlayoutEnabled = request.PlayoutEnabled;
            settings.MaxLibrarySize = Math.Clamp(request.MaxLibrarySize, 5, 5000);
            settings.MinTrackDurationSeconds = Math.Clamp(request.MinTrackDurationSeconds, 30, 600);
            settings.MaxTrackDurationSeconds = Math.Clamp(request.MaxTrackDurationSeconds, settings.MinTrackDurationSeconds, 600);
            settings.EnableBreathMarkers = request.EnableBreathMarkers;
            settings.FrequencyMhz = Math.Clamp(request.FrequencyMhz, 76, 108);
            settings.FirstDayOfWeek = request.FirstDayOfWeek is 0 or 1 ? request.FirstDayOfWeek : 1;
            settings.TextProvider = request.TextProvider == TextProviders.OpenAi ? TextProviders.OpenAi : TextProviders.Ollama;
            settings.OpenAiApiKey = request.OpenAiApiKey ?? string.Empty;
            settings.OpenAiModel = string.IsNullOrWhiteSpace(request.OpenAiModel) ? settings.OpenAiModel : request.OpenAiModel.Trim();
            settings.ElevenLabsEnabled = request.ElevenLabsEnabled;
            settings.ElevenLabsApiKey = request.ElevenLabsApiKey ?? string.Empty;
            settings.GreetingsEnabled = request.GreetingsEnabled;

            await db.SaveChangesAsync(ct);

            // Language changed → every host follows the station language.
            if (!string.Equals(previousLanguage, settings.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                await aligner.AlignAsync(ct);
            }

            return Results.Ok(ToDto(settings));
        });
    }

    private static void MapFormatsAndSchedule(RouteGroupBuilder api)
    {
        api.MapGet("/formats", async (RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var formats = await db.Formats.AsNoTracking().Include(f => f.Moderator)
                .OrderByDescending(f => f.IsEnabled).ThenBy(f => f.Name)
                .ToListAsync(ct);
            var slots = await db.ProgramSlots.AsNoTracking().Where(s => s.FormatId != null).ToListAsync(ct);

            var now = time.GetLocalNow();
            return Results.Ok(formats.Select(f => new FormatDto(
                f.Id, f.Name, f.Description, f.Genre, f.Subgenre,
                f.Moderator?.Name, f.ModeratorId, f.Reason, f.IsEnabled, f.UpVotes, f.DownVotes,
                NextOnAir(slots.Where(s => s.FormatId == f.Id), now), f.Talkativeness)).ToList());
        });

        api.MapPost("/formats/{id:guid}/toggle", async (Guid id, RadioDbContext db, CancellationToken ct) =>
        {
            var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (format is null)
            {
                return Results.NotFound();
            }

            format.IsEnabled = !format.IsEnabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { format.Id, format.IsEnabled });
        });

        api.MapPost("/formats/{id:guid}/vote", async (Guid id, int direction, RadioDbContext db, CancellationToken ct) =>
        {
            var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (format is null)
            {
                return Results.NotFound();
            }

            if (direction > 0)
            {
                format.UpVotes++;
            }
            else
            {
                format.DownVotes++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { format.Id, format.UpVotes, format.DownVotes });
        });

        api.MapGet("/schedule", async (RadioDbContext db, CancellationToken ct) =>
        {
            var slots = await db.ProgramSlots.AsNoTracking()
                .Include(s => s.Format!).ThenInclude(f => f.Moderator)
                .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartMinute)
                .ToListAsync(ct);

            return Results.Ok(slots.Select(s => new ProgramSlotDto(
                s.Id, s.DayOfWeek, s.StartMinute, s.DurationMinutes, s.FormatId,
                s.Format?.Name, s.Format?.Moderator?.Name,
                s.Format is null ? null : string.IsNullOrEmpty(s.Format.Subgenre) ? s.Format.Genre : s.Format.Subgenre)).ToList());
        });
    }

    private static void MapStats(RouteGroupBuilder api)
    {
        api.MapGet("/stats", async (RadioDbContext db, IHttpClientFactory httpFactory,
            IOptions<IcecastOptions> icecast, CancellationToken ct) =>
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

            var hourAgo = DateTime.UtcNow.AddHours(-1);
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

    private static void MapConsole(RouteGroupBuilder api)
    {
        api.MapGet("/console", (InMemoryLogBuffer buffer) =>
            Results.Ok(buffer.Snapshot()
                .Select(e => new ConsoleLineDto(e.TimestampUtc, e.Level, e.Category, e.Message))
                .ToList()));

        api.MapPost("/admin/director/run", (DirectorControl control) =>
        {
            control.TriggerRun();
            return Results.Ok(new { triggered = true, lastRunUtc = control.LastRunUtc });
        });
    }

    private static string? NextOnAir(IEnumerable<ProgramSlot> slots, DateTimeOffset now)
    {
        var best = slots
            .Select(s =>
            {
                var daysAhead = ((s.DayOfWeek - (int)now.DayOfWeek) % 7 + 7) % 7;
                var start = now.Date.AddDays(daysAhead).AddMinutes(s.StartMinute);
                if (start < now.DateTime)
                {
                    start = start.AddDays(7);
                }

                return start;
            })
            .OrderBy(s => s)
            .Cast<DateTime?>()
            .FirstOrDefault();

        return best?.ToString("ddd HH:mm");
    }

    private static TrackDto ToDto(Track t) => new(
        t.Id, t.Title, t.Genre, t.Subgenre, t.Artist?.Name ?? "—", t.ArtistId, t.HasVocals,
        t.DurationSeconds, t.PlayCount, t.UpVotes, t.DownVotes, t.IsRetired, t.Backend, t.CreatedAt);

    private static ModeratorDto ToDto(Moderator m) => new(
        m.Id, m.Name, m.Language, m.Gender, m.TtsEngine, m.VoiceId, m.SpeechRate, m.Style,
        m.PersonaPrompt, m.PrefersVocals, m.PreferredGenres, m.IsActive, m.IsAutoGenerated, m.Talkativeness);

    private static StationSettingsDto ToDto(StationSettings s) => new(
        s.StationName, s.DefaultLanguage, s.TargetQueueLength, s.AnnouncementEveryNTracks,
        s.MusicProductionEnabled, s.PlayoutEnabled, s.MaxLibrarySize,
        s.MinTrackDurationSeconds, s.MaxTrackDurationSeconds, s.EnableBreathMarkers,
        s.FrequencyMhz, s.FirstDayOfWeek, s.TextProvider, s.OpenAiApiKey, s.OpenAiModel,
        s.ElevenLabsEnabled, s.ElevenLabsApiKey, s.GreetingsEnabled);

    private static string HashClient(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private sealed record IcecastStatus([property: JsonPropertyName("icestats")] IceStats? IceStats);

    private sealed record IceStats([property: JsonPropertyName("source")] IcecastSource? Source);

    private sealed record IcecastSource(
        [property: JsonPropertyName("listeners")] int Listeners,
        [property: JsonPropertyName("listener_peak")] int ListenerPeak);
}
