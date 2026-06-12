using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
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
                    agg?.Count ?? 0, agg?.Up ?? 0, agg?.Down ?? 0, a.IsRetired, a.Biography);
            }).ToList());
        });

        // Artist detail; writes the biography on first view for artists that
        // predate biographies (LLM call — can take a moment).
        api.MapGet("/artists/{id:guid}", async (
            Guid id, RadioDbContext db, MusicCopywriter copywriter, CancellationToken ct) =>
        {
            var artist = await db.Artists.FirstOrDefaultAsync(a => a.Id == id, ct);
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

            return Results.Ok(new ArtistDto(artist.Id, artist.Name, artist.Genre, artist.Subgenre,
                artist.StyleDescriptor, stats?.Count ?? 0, stats?.Up ?? 0, stats?.Down ?? 0,
                artist.IsRetired, artist.Biography));
        });

        // "Create new song" — queued for the production loop, generated in the
        // artist's signature style. 202: poll /music/status for progress.
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

        // In-library preview playback — intentionally does NOT touch PlayCount;
        // only broadcast plays count.
        api.MapGet("/library/{id:guid}/audio", async (
            Guid id, RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (track is null)
            {
                return Results.NotFound();
            }

            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, track.FilePath);
            if (!System.IO.File.Exists(absolutePath))
            {
                return Results.NotFound();
            }

            return Results.File(absolutePath, "audio/wav", enableRangeProcessing: true);
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

        MapStudios(api);
        MapMixer(api);
        MapVoices(api);

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
            settings.DefaultMusicProvider = MusicBackends.IsKnown(request.DefaultMusicProvider)
                ? MusicBackends.Normalize(request.DefaultMusicProvider)
                : MusicBackends.MusicGen;
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

    private static void MapVoices(RouteGroupBuilder api)
    {
        // Mint a reproducible voice from a text description (Qwen Voice-Design).
        api.MapPost("/voices/design", async (
            DesignVoiceDto request, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return Results.BadRequest("A voice description is required.");
            }

            try
            {
                var voice = await designer.DesignVoiceAsync(
                    request.Description, request.Gender, request.Language, ct);
                return Results.Ok(new DesignedVoiceDto(voice.Handle, request.Description, voice.DurationSeconds));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        api.MapGet("/voices/{handle}/preview", async (
            string handle, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            try
            {
                var wav = await designer.GetPreviewAsync(handle, ct);
                return Results.File(wav, "audio/wav");
            }
            catch (HttpRequestException)
            {
                return Results.NotFound();
            }
        });

        // One-click upgrade: mint a Qwen voice from the host's persona. Returns
        // the handle for preview — applying it is a separate, explicit step.
        api.MapPost("/moderators/{id:int}/redesign-voice", async (
            int id, RadioDbContext db, IVoiceDesignClient designer, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            var description = !string.IsNullOrWhiteSpace(moderator.VoiceDescription)
                ? moderator.VoiceDescription
                : $"{moderator.Style} radio host. "
                    + moderator.PersonaPrompt[..Math.Min(160, moderator.PersonaPrompt.Length)];

            try
            {
                var voice = await designer.DesignVoiceAsync(description, moderator.Gender, moderator.Language, ct);
                return Results.Ok(new DesignedVoiceDto(voice.Handle, description, voice.DurationSeconds));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        // Applies a designed voice to a host (reversible: old engine/voice are
        // simply overwritten; redesign again or re-pick a preset to revert).
        api.MapPost("/moderators/{id:int}/apply-voice", async (
            int id, ApplyVoiceDto request, RadioDbContext db, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.TtsEngine = TtsEngines.Qwen;
            moderator.VoiceId = request.Handle;
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                moderator.VoiceDescription = request.Description;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });
    }

    private static void MapMixer(RouteGroupBuilder api)
    {
        api.MapGet("/mixer", async (RadioDbContext db, MixerDiagnostics diagnostics, CancellationToken ct) =>
        {
            var s = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
            var settings = new MixerSettingsDto(
                s.MixerEnabled, s.TargetLufs, s.MaxMakeupGainDb, s.DuckLevelDb, s.DuckRampMs,
                s.DefaultCrossfadeSeconds, s.BeatAlignBpmTolerancePct,
                s.HardCutGapAfterTalkMsMin, s.HardCutGapAfterTalkMsMax,
                s.HardCutGapSongMsMin, s.HardCutGapSongMsMax,
                s.PostHitSafetyMs, s.StrategyWeightsJson, s.AnalysisRequired);

            var analyzedTracks = await db.MediaAnalyses
                .CountAsync(a => a.ItemType == PlayoutItemType.Track && a.AnalyzerVersion > 0, ct);
            var totalTracks = await db.Tracks.CountAsync(t => !t.IsRetired, ct);
            var analyzedAnnouncements = await db.MediaAnalyses
                .CountAsync(a => a.ItemType == PlayoutItemType.Announcement && a.AnalyzerVersion > 0, ct);

            var byStrategy = await db.TransitionLog.AsNoTracking()
                .GroupBy(e => e.Strategy)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var totalClips = await db.TransitionLog.AsNoTracking().SumAsync(e => (int?)e.ClipCount, ct) ?? 0;

            var recent = await db.TransitionLog.AsNoTracking()
                .OrderByDescending(e => e.OccurredAt)
                .Take(20)
                .ToListAsync(ct);

            var trackTitles = await db.Tracks.AsNoTracking()
                .Where(t => recent.Select(r => r.OutgoingId).Concat(recent.Select(r => r.IncomingId)).Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Title, ct);
            var announcementKinds = await db.Announcements.AsNoTracking()
                .Where(a => recent.Select(r => r.OutgoingId).Concat(recent.Select(r => r.IncomingId)).Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Kind.ToString(), ct);

            string Title(PlayoutItemType type, Guid id) => type == PlayoutItemType.Track
                ? trackTitles.GetValueOrDefault(id, "track")
                : $"{announcementKinds.GetValueOrDefault(id, "talk")} (talk)";

            string? Trace(string parametersJson)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(parametersJson);
                    return doc.RootElement.TryGetProperty("reasonTrace", out var t) ? t.GetString() : null;
                }
                catch
                {
                    return null;
                }
            }

            var status = new MixerStatusDto(
                analyzedTracks, totalTracks, analyzedAnnouncements, byStrategy, totalClips,
                recent.Select(e => new TransitionLogEntryDto(
                    e.OccurredAt, e.Strategy,
                    Title(e.OutgoingType, e.OutgoingId), Title(e.IncomingType, e.IncomingId),
                    e.OverlapSeconds, e.GapMs, e.ClipCount, Trace(e.ParametersJson))).ToList());

            var live = diagnostics.Snapshot();
            return Results.Ok(new MixerOverviewDto(settings, status, new MixerLiveDto(
                live.Active, live.EngagedAtUtc, live.MasterSeconds, live.ActiveItems,
                live.LastDecision, live.LastDecisionAtUtc, live.Transitions)));
        });

        api.MapPut("/mixer/settings", async (MixerSettingsDto request, RadioDbContext db, CancellationToken ct) =>
        {
            if (!WhipRadio.Core.Audio.MixPlanner.TryValidateWeightsJson(request.StrategyWeightsJson, out var error))
            {
                return Results.BadRequest($"Strategy weights: {error}");
            }

            var s = await db.StationSettings.FirstOrDefaultAsync(ct);
            if (s is null)
            {
                return Results.NotFound();
            }

            s.MixerEnabled = request.MixerEnabled;
            s.TargetLufs = Math.Clamp(request.TargetLufs, -30, -8);
            s.MaxMakeupGainDb = Math.Clamp(request.MaxMakeupGainDb, 0, 12);
            s.DuckLevelDb = Math.Clamp(request.DuckLevelDb, -30, 0);
            s.DuckRampMs = Math.Clamp(request.DuckRampMs, 50, 5000);
            s.DefaultCrossfadeSeconds = Math.Clamp(request.DefaultCrossfadeSeconds, 1, 15);
            s.BeatAlignBpmTolerancePct = Math.Clamp(request.BeatAlignBpmTolerancePct, 0, 20);
            s.HardCutGapAfterTalkMsMin = Math.Clamp(request.HardCutGapAfterTalkMsMin, 0, 5000);
            s.HardCutGapAfterTalkMsMax = Math.Clamp(
                Math.Max(request.HardCutGapAfterTalkMsMax, request.HardCutGapAfterTalkMsMin), 0, 5000);
            s.HardCutGapSongMsMin = Math.Clamp(request.HardCutGapSongMsMin, 0, 5000);
            s.HardCutGapSongMsMax = Math.Clamp(
                Math.Max(request.HardCutGapSongMsMax, request.HardCutGapSongMsMin), 0, 5000);
            s.PostHitSafetyMs = Math.Clamp(request.PostHitSafetyMs, 0, 5000);
            s.StrategyWeightsJson = request.StrategyWeightsJson;
            s.AnalysisRequired = request.AnalysisRequired;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // "Re-run backfill": drop stub rows (failed analyses) so the backfill
        // service picks them up on its next cycle.
        api.MapPost("/mixer/backfill", async (RadioDbContext db, CancellationToken ct) =>
        {
            var removed = await db.MediaAnalyses.Where(a => a.AnalyzerVersion == 0).ExecuteDeleteAsync(ct);
            return Results.Ok(new { removedStubs = removed });
        });
    }

    private static void MapStudios(RouteGroupBuilder api)
    {
        api.MapGet("/studios", async (RadioDbContext db, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var studios = await db.Studios.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct);
            var jobs = coordinator.ActiveJobs;
            return Results.Ok(studios.Select(s =>
            {
                var job = jobs.TryGetValue(s.Id, out var j) ? j : null;
                return ToStudioDto(s, job);
            }).ToList());
        });

        api.MapPost("/studios/test", async (TestStudioDto request, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var (ok, provider, detail) = await coordinator.TestAsync(
                ParseStudioKind(request.Kind), request.Source, request.Url, request.Provider, request.ApiKey, ct);
            return Results.Ok(new StudioTestResultDto(ok, provider, detail));
        });

        api.MapPost("/studios", async (SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var kind = ParseStudioKind(request.Kind);
            var (ok, provider, detail) = await coordinator.TestAsync(
                kind, request.Source, request.Url, request.Provider, request.ApiKey, ct);
            if (!ok)
            {
                return Results.BadRequest(detail ?? "Connection test failed.");
            }

            var isApi = string.Equals(request.Source, "api", StringComparison.OrdinalIgnoreCase);
            var count = await db.Studios.CountAsync(s => s.Kind == kind, ct);
            var studio = new Studio
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(request.Name)
                    ? DefaultStudioName(kind, count + 1)
                    : request.Name.Trim(),
                Kind = kind,
                Url = isApi ? string.Empty : request.Url!.TrimEnd('/'),
                Provider = provider!,
                ApiKey = isApi ? request.ApiKey : null,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };

            db.Studios.Add(studio);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPut("/studios/{id:guid}", async (Guid id, SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var studio = await db.Studios.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            var (ok, provider, detail) = await coordinator.TestAsync(
                studio.Kind, request.Source, request.Url, request.Provider, request.ApiKey, ct);
            if (!ok)
            {
                return Results.BadRequest(detail ?? "Connection test failed.");
            }

            var isApi = string.Equals(request.Source, "api", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                studio.Name = request.Name.Trim();
            }

            studio.Url = isApi ? string.Empty : request.Url!.TrimEnd('/');
            studio.Provider = provider!;
            studio.ApiKey = isApi ? request.ApiKey : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPost("/studios/{id:guid}/toggle", async (Guid id, RadioDbContext db, CancellationToken ct) =>
        {
            var studio = await db.Studios.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            studio.IsActive = !studio.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapDelete("/studios/{id:guid}", async (Guid id, RadioDbContext db,
            StudioCoordinator coordinator, CancellationToken ct) =>
        {
            if (coordinator.ActiveJobs.ContainsKey(id))
            {
                return Results.Conflict("Studio is recording right now — wait for the job to finish.");
            }

            var deleted = await db.Studios.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private static StudioKind ParseStudioKind(string kind)
        => Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsed) ? parsed : StudioKind.Recording;

    private static string DefaultStudioName(StudioKind kind, int number)
        => kind == StudioKind.VoiceBooth ? $"Booth #{number}" : $"Studio #{number}";

    private static StudioDto ToStudioDto(Studio s, StudioJob? job) => new(
        s.Id, s.Name, s.Kind.ToString(), s.Url, s.Provider, s.IsActive,
        s.CreatedAt, s.LastUsedAt, s.JobsCompleted, s.JobsFailed,
        job?.Label, job?.StartedAtUtc);

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

        api.MapGet("/serverstats", async (ServerStatsCollector collector, CancellationToken ct) =>
            Results.Ok(await collector.CollectAsync(ct)));
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
        s.FrequencyMhz, s.FirstDayOfWeek, MusicBackends.Normalize(s.DefaultMusicProvider),
        s.TextProvider, s.OpenAiApiKey, s.OpenAiModel,
        s.ElevenLabsEnabled, s.ElevenLabsApiKey, s.GreetingsEnabled);

    private static string HashClient(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private sealed record IcecastStatus([property: JsonPropertyName("icestats")] IceStats? IceStats);

    private sealed record IceStats([property: JsonPropertyName("source")] IcecastSource? Source);

    private sealed record IcecastSource(
        [property: JsonPropertyName("listeners")] int Listeners,
        [property: JsonPropertyName("listener_peak")] int ListenerPeak);
}
