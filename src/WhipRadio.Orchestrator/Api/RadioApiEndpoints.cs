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

public static class RadioApiEndpoints
{
    public static IEndpointRouteBuilder MapRadioApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        MapNowPlaying(api);
        MapStationStatus(api);
        MapLibrary(api);
        MapArtistPosts(api);
        MapPlayLog(api);
        MapTalkBreaks(api);
        MapVotes(api);
        MapModerators(api);
        MapSettings(api);
        MapProduction(api);
        MapBranding(api);
        MapFormatsAndSchedule(api);
        MapStats(api);
        MapConsole(api);
        MapPrivacy(api);

        return app;
    }

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
                var voicedText = announcement?.VoicedText;
                transcript = voicedText is null ? null : SpeechMarkerNormalizer.ToPlainText(voicedText);
                announcementKind = announcement?.Kind.ToString();
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
            return Results.Ok(new StationStatusDto(info.Status.ToString(), info.Reason, info.NextAttemptUtc));
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
                .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography))
                .ToListAsync(ct);

            return Results.Ok(new ArtistDto(
                artist.Id, artist.Name, artist.Slug, artist.Genre, artist.Subgenre, artist.StyleDescriptor,
                0, 0, 0, artist.IsRetired, artist.Biography,
                artist.Type, artist.Origin, artist.FormationYear, artist.PromotionText, members, artist.Language));
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
                    .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography))
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
                    .Select(m => new ArtistMemberDto(m.Id, m.Name, m.Role, m.Biography))
                    .ToList(),
                artist.Language));
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
                    title = talkBreaks.ContainsKey(e.ItemId) ? "Announcement" : announcement?.Kind.ToString() ?? "(announcement)";
                    transcript = announcement?.VoicedText is { } voiced
                        ? SpeechMarkerNormalizer.ToPlainText(voiced)
                        : null;
                }

                return new PlayLogEntryDto(
                    e.PlayedAt, e.ItemType.ToString(), e.ItemId, title,
                    e.ModeratorId is int id ? moderatorNames.GetValueOrDefault(id) : null,
                    e.DurationSeconds, transcript,
                    talkBreaks.TryGetValue(e.ItemId, out var talkBreak)
                        ? talkBreak.Parts
                            .OrderBy(part => part.SortOrder)
                            .Select(ToDto)
                            .ToList()
                        : null);
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

    private static void MapTalkBreaks(RouteGroupBuilder api)
    {
        api.MapPost("/talkbreaks/emergency", async (
            EmergencyTalkBreakRequestDto request,
            RadioDbContext db,
            ScheduleService schedule,
            AnnouncementFactory factory,
            PriorityTalkBreakDispatcher dispatcher,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest("Text is required.");
            }

            var priority = ParseOnDemandPriority(request.Priority);
            Moderator? moderator;
            if (request.ModeratorId is int moderatorId)
            {
                moderator = await db.Moderators.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == moderatorId && item.IsActive, ct);
                if (moderator is null)
                {
                    return Results.NotFound();
                }
            }
            else
            {
                moderator = (await schedule.GetCurrentAsync(ct)).Moderator;
            }

            var stationName = (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).StationName;
            var expiresInMinutes = Math.Clamp(request.ExpiresInMinutes ?? 60, 5, 24 * 60);
            var announcement = await factory.ProduceDirectAsync(
                AnnouncementKind.EmergencyMessage,
                TalkPartKind.EmergencyMessage,
                priority,
                moderator,
                request.Text,
                "EmergencyMessage",
                ct,
                expiresAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddMinutes(expiresInMinutes));

            var talkBreakId = await db.TalkBreaks.AsNoTracking()
                .Where(talkBreak => talkBreak.AnnouncementId == announcement.Id)
                .Select(talkBreak => talkBreak.Id)
                .FirstAsync(ct);

            await dispatcher.PushReadyAsync(ct);

            return Results.Accepted(
                $"/api/announcements/{announcement.Id}/audio",
                new EmergencyTalkBreakDto(
                    announcement.Id,
                    talkBreakId,
                    priority.ToString(),
                    TalkBreakStatus.Rendered.ToString()));
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
        api.MapGet("/moderators", async (RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var moderators = await db.Moderators.AsNoTracking()
                .OrderBy(m => m.Name)
                .ThenBy(m => m.Id)
                .ToListAsync(ct);
            var now = time.GetLocalNow();
            return Results.Ok(moderators.Select(m => ToDto(m, now)).ToList());
        });

        api.MapPost("/moderators", async (CreateModeratorDto request, RadioDbContext db,
            IVoiceDesignClient voiceDesigner, IProductionUpdatePublisher productionUpdates, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            // Hosts always speak the station language (the main language).
            var stationLanguage = StationLanguages.Normalize(
                (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).DefaultLanguage);
            var baselineTraits = ParseBaselineTraits(request.BaselineTraits, request.Style, request.Talkativeness);
            var existingSlugs = await db.Moderators.AsNoTracking()
                .Select(m => m.Slug)
                .ToListAsync(ct);

            var moderator = new Moderator
            {
                Name = request.Name.Trim(),
                Slug = SlugGenerator.UniqueFromName(request.Name, existingSlugs),
                Language = stationLanguage,
                Gender = request.Gender == ModeratorGenders.Male ? ModeratorGenders.Male : ModeratorGenders.Female,
                TtsEngine = TtsEngines.Qwen,
                Style = request.Style,
                PersonaPrompt = request.PersonaPrompt,
                PrefersVocals = request.PrefersVocals,
                PreferredGenres = request.PreferredGenres,
                Talkativeness = Math.Clamp(request.Talkativeness, 0, 1),
                IsWeatherSpecialist = request.IsWeatherSpecialist,
                IsNewsSpecialist = request.IsNewsSpecialist,
                BaselineEnergy = baselineTraits.Energy,
                BaselineFormality = baselineTraits.Formality,
                BaselineHumorLevel = baselineTraits.HumorLevel,
                BaselineTalkativeness = baselineTraits.Talkativeness,
                BaselineWarmth = baselineTraits.Warmth,
                PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim(),
                VoiceDescription = SanitizeOptional(
                    request.VoiceDescription,
                    BuildModeratorVoiceDescription(request.Name, request.Gender, request.Style, request.PersonaPrompt)),
                IsActive = true,
                SpeechRate = 1.0,
            };
            ApplyTalkProfile(moderator, request.TalkProfile);

            try
            {
                var voice = await voiceDesigner.DesignVoiceAsync(
                    moderator.VoiceDescription,
                    moderator.Gender,
                    moderator.Language,
                    BuildVoiceIntroSample(moderator.Name, moderator.Language),
                    ct);
                moderator.VoiceId = voice.Handle;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(
                    "Voice design booth is unreachable. Check the active Voice Booth on the Studios page.",
                    statusCode: 503);
            }

            db.Moderators.Add(moderator);
            await db.SaveChangesAsync(ct);
            if (moderator.IsNewsSpecialist)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            if (moderator.IsWeatherSpecialist)
            {
                await productionUpdates.PublishWeatherChangedAsync(ct);
            }

            return Results.Ok(ToDto(moderator, time.GetLocalNow()));
        });

        api.MapPost("/moderators/specialist", async (
            CreateSpecialistHostRequestDto request,
            SpecialistHostCreationService specialistHosts,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<SpecialistHostRole>(request.Role, ignoreCase: true, out var role)
                || role is not (SpecialistHostRole.News or SpecialistHostRole.Weather))
            {
                return Results.BadRequest("Role must be News or Weather.");
            }

            try
            {
                var moderator = await specialistHosts.CreateAsync(role, request.Hint, ct);
                return Results.Ok(ToDto(moderator, time.GetLocalNow()));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(
                    "Host creation timed out or the writer room / voice booth is unreachable.",
                    statusCode: 503);
            }
        });

        api.MapPost("/moderators/{id:int}/toggle", async (int id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.IsActive = !moderator.IsActive;
            await db.SaveChangesAsync(ct);
            if (moderator.IsNewsSpecialist)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            if (moderator.IsWeatherSpecialist)
            {
                await productionUpdates.PublishWeatherChangedAsync(ct);
            }

            return Results.Ok(new { moderator.Id, moderator.IsActive });
        });

        api.MapGet("/moderators/{id:int}/usage", async (int id, RadioDbContext db, CancellationToken ct) =>
        {
            if (!await db.Moderators.AsNoTracking().AnyAsync(m => m.Id == id, ct))
            {
                return Results.NotFound();
            }

            return Results.Ok(await BuildModeratorUsageAsync(db, id, ct));
        });

        api.MapPost("/moderators/{id:int}/fire", async (int id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, TimeProvider time, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            var usage = await BuildModeratorUsageAsync(db, id, ct);
            var now = DateTime.UtcNow;
            moderator.IsActive = false;

            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is not null)
            {
                if (settings.NewsPresenterModeratorId == id)
                {
                    settings.NewsPresenterModeratorId = null;
                }

                if (settings.WeatherSpecialistModeratorId == id)
                {
                    settings.WeatherSpecialistModeratorId = null;
                }
            }

            await db.Formats
                .Where(format => format.ModeratorId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(format => format.ModeratorId, (int?)null), ct);

            await db.ListenerMessages
                .Where(message => message.ModeratorId == id
                    && (message.Status == ListenerMessageStatus.Pending || message.Status == ListenerMessageStatus.Queued))
                .ExecuteUpdateAsync(update => update.SetProperty(message => message.ModeratorId, (int?)null), ct);

            await db.TalkBits
                .Where(bit => bit.ModeratorId == id && bit.Status == TalkBitStatus.Active)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(bit => bit.Status, TalkBitStatus.Retired)
                    .SetProperty(bit => bit.RetiredAtUtc, now)
                    .SetProperty(bit => bit.RetirementReason, "Host fired"), ct);

            await db.TalkParts
                .Where(part => db.TalkBreaks.Any(talkBreak => talkBreak.Id == part.TalkBreakId
                    && talkBreak.ModeratorId == id
                    && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered)))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(part => part.Status, TalkPartStatus.Expired)
                    .SetProperty(part => part.ExpiresAtUtc, now), ct);

            await db.TalkBreaks
                .Where(talkBreak => talkBreak.ModeratorId == id
                    && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(talkBreak => talkBreak.Status, TalkBreakStatus.Expired)
                    .SetProperty(talkBreak => talkBreak.ExpiresAtUtc, now), ct);

            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            await productionUpdates.PublishWeatherChangedAsync(ct);

            return Results.Ok(new FireModeratorResultDto(ToDto(moderator, time.GetLocalNow()), usage));
        });

        api.MapPut("/moderators/{id:int}/photo", async (int id, ModeratorPhotoDto request,
            RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var moderator = await db.Moderators.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (moderator is null)
            {
                return Results.NotFound();
            }

            moderator.PhotoUrl = string.IsNullOrWhiteSpace(request.PhotoUrl) ? null : request.PhotoUrl.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(moderator, time.GetLocalNow()));
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
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            return Results.Ok(ToDto(settings));
        });

        MapStudios(api);
        MapStudioHistory(api);
        MapMixer(api);
        MapVoices(api);

        api.MapPut("/settings", async (StationSettingsDto request, RadioDbContext db,
            HostLanguageAligner aligner, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            var previousLanguage = settings.DefaultLanguage;
            settings.StationName = string.IsNullOrWhiteSpace(request.StationName) ? settings.StationName : request.StationName.Trim();
            settings.StationSlogan = SanitizeOptional(request.StationSlogan, settings.StationSlogan);
            settings.StationVision = SanitizeOptional(request.StationVision, settings.StationVision);
            settings.StationMission = SanitizeOptional(request.StationMission, settings.StationMission);
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
            settings.WeatherEnabled = request.WeatherEnabled;
            settings.WeatherCadenceMinutes = WeatherScheduler.NormalizeCadence(request.WeatherCadenceMinutes);
            settings.WeatherFullHandoverEnabled = request.WeatherFullHandoverEnabled;
            settings.WeatherLocationName = SanitizeOptional(request.WeatherLocationName, settings.WeatherLocationName);
            settings.WeatherLatitude = Math.Clamp(request.WeatherLatitude, -90, 90);
            settings.WeatherLongitude = Math.Clamp(request.WeatherLongitude, -180, 180);
            settings.WeatherSpecialistModeratorId = request.WeatherSpecialistModeratorId is int specialistId
                && await db.Moderators.AsNoTracking()
                    .AnyAsync(m => m.Id == specialistId && m.IsActive && m.IsWeatherSpecialist, ct)
                    ? specialistId
                    : null;

            await db.SaveChangesAsync(ct);

            // Language changed → every host follows the station language.
            if (!string.Equals(previousLanguage, settings.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                await aligner.AlignAsync(ct);
            }

            return Results.Ok(ToDto(settings));
        });
    }

    private static void MapProduction(RouteGroupBuilder api)
    {
        api.MapGet("/production/news", async (RadioDbContext db, TimeProvider timeProvider, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
            var categoryOrder = NewsCategoryOrdering.Parse(settings.NewsCategoryOrder);
            var itemCounts = await db.NewsItems.AsNoTracking()
                .GroupBy(item => item.FeedId)
                .Select(group => new { FeedId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.FeedId, group => group.Count, ct);
            var feeds = await db.NewsFeeds.AsNoTracking()
                .ToListAsync(ct);
            var packages = await db.NewsPackages.AsNoTracking()
                .OrderByDescending(package => package.TargetUtc)
                .Take(12)
                .ToListAsync(ct);
            var nextPlan = NewsPackageProductionService.ResolveNextPackagePlan(settings, timeProvider.GetLocalNow());
            var nextTargetUtc = nextPlan.TargetLocal.UtcDateTime;
            var nextPackageStatus = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc == nextTargetUtc
                    && package.Status != NewsPackageStatus.Failed)
                .OrderByDescending(package => package.CreatedAtUtc)
                .Select(package => package.Status.ToString())
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new NewsProductionDto(
                settings.NewsEnabled,
                settings.NewsExtractionEnabled,
                TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes),
                Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60),
                settings.NewsPresenterModeratorId,
                TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds),
                TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds),
                nextTargetUtc,
                nextPackageStatus,
                categoryOrder,
                BuildProductionWarning(settings, moderators),
                NewsCategoryOrdering.SortFeeds(feeds, categoryOrder)
                    .Select(feed => ToDto(feed, itemCounts.GetValueOrDefault(feed.Id)))
                    .ToList(),
                packages.Select(ToDto).ToList()));
        });

        api.MapPut("/production/news/settings", async (
            SaveNewsProductionSettingsDto request,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            settings.NewsEnabled = request.NewsEnabled;
            settings.NewsExtractionEnabled = request.NewsExtractionEnabled;
            settings.NewsPackageCadenceMinutes = TopOfHourScheduler.NormalizeCadence(request.NewsPackageCadenceMinutes);
            settings.NewsPackageMaxDurationSeconds = Math.Clamp(request.NewsPackageMaxDurationSeconds, 60, 30 * 60);
            settings.TopOfHourFadeOutSeconds = TopOfHourScheduler.NormalizeFadeOutSeconds(request.TopOfHourFadeOutSeconds);
            settings.TopOfHourIntroGraceSeconds = TopOfHourScheduler.NormalizeIntroGraceSeconds(request.TopOfHourIntroGraceSeconds);
            settings.NewsCategoryOrder = NewsCategoryOrdering.ToStorage(request.NewsCategoryOrder);
            settings.NewsPresenterModeratorId = request.NewsPresenterModeratorId is int presenterId
                && await db.Moderators.AsNoTracking()
                    .AnyAsync(m => m.Id == presenterId && m.IsActive && m.IsNewsSpecialist, ct)
                    ? presenterId
                    : null;

            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            await productionUpdates.PublishWeatherChangedAsync(ct);
            return Results.NoContent();
        });

        api.MapPost("/production/news/packages/next", async (
            NewsPackageProductionService production,
            CancellationToken ct) =>
        {
            var package = await production.ProduceNextPackageAsync(ct);
            return package is null
                ? Results.BadRequest("No fresh news items are available for a package.")
                : Results.Ok(ToDto(package));
        });

        api.MapPost("/production/news/packages/{id:guid}/recreate", async (
            Guid id,
            NewsPackageProductionService production,
            CancellationToken ct) =>
        {
            try
            {
                var package = await production.RecreatePackageAsync(id, ct);
                return package is null
                    ? Results.BadRequest("No fresh news items are available for a replacement package.")
                    : Results.Ok(ToDto(package));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        api.MapPost("/news/feeds", async (SaveNewsFeedDto request, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, CancellationToken ct) =>
        {
            if (!TryNormalizeFeed(request, out var normalized, out var error))
            {
                return Results.BadRequest(error);
            }

            if (await db.NewsFeeds.AnyAsync(feed => feed.Url == normalized.Url, ct))
            {
                return Results.Conflict("A feed with this URL already exists.");
            }

            var feed = new NewsFeed
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = DateTime.UtcNow,
            };
            Apply(feed, normalized);
            db.NewsFeeds.Add(feed);
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return Results.Ok(ToDto(feed, itemCount: 0));
        });

        api.MapPut("/news/feeds/{id:guid}", async (
            Guid id,
            SaveNewsFeedDto request,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            if (!TryNormalizeFeed(request, out var normalized, out var error))
            {
                return Results.BadRequest(error);
            }

            var feed = await db.NewsFeeds.FirstOrDefaultAsync(feed => feed.Id == id, ct);
            if (feed is null)
            {
                return Results.NotFound();
            }

            if (await db.NewsFeeds.AnyAsync(candidate => candidate.Id != id && candidate.Url == normalized.Url, ct))
            {
                return Results.Conflict("A feed with this URL already exists.");
            }

            Apply(feed, normalized);
            await db.SaveChangesAsync(ct);
            var itemCount = await db.NewsItems.CountAsync(item => item.FeedId == id, ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return Results.Ok(ToDto(feed, itemCount));
        });

        api.MapPost("/news/feeds/{id:guid}/toggle", async (Guid id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, CancellationToken ct) =>
        {
            var feed = await db.NewsFeeds.FirstOrDefaultAsync(feed => feed.Id == id, ct);
            if (feed is null)
            {
                return Results.NotFound();
            }

            feed.IsEnabled = !feed.IsEnabled;
            await db.SaveChangesAsync(ct);
            var itemCount = await db.NewsItems.CountAsync(item => item.FeedId == id, ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return Results.Ok(ToDto(feed, itemCount));
        });

        api.MapDelete("/news/feeds/{id:guid}", async (Guid id, RadioDbContext db,
            IProductionUpdatePublisher productionUpdates, CancellationToken ct) =>
        {
            var deleted = await db.NewsFeeds.Where(feed => feed.Id == id).ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        api.MapGet("/production/weather", async (RadioDbContext db, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
            return Results.Ok(ToWeatherProductionDto(settings, moderators));
        });

        api.MapPut("/production/weather", async (
            SaveWeatherProductionSettingsDto request,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            settings.WeatherEnabled = request.WeatherEnabled;
            settings.WeatherCadenceMinutes = WeatherScheduler.NormalizeCadence(request.WeatherCadenceMinutes);
            settings.WeatherFullHandoverEnabled = request.WeatherFullHandoverEnabled;
            settings.WeatherLocationName = SanitizeOptional(request.WeatherLocationName, settings.WeatherLocationName);
            settings.WeatherLatitude = Math.Clamp(request.WeatherLatitude, -90, 90);
            settings.WeatherLongitude = Math.Clamp(request.WeatherLongitude, -180, 180);
            settings.WeatherSpecialistModeratorId = request.WeatherSpecialistModeratorId is int specialistId
                && await db.Moderators.AsNoTracking()
                    .AnyAsync(m => m.Id == specialistId && m.IsActive && m.IsWeatherSpecialist, ct)
                    ? specialistId
                    : null;

            await db.SaveChangesAsync(ct);
            var moderators = await db.Moderators.AsNoTracking().ToListAsync(ct);
            await productionUpdates.PublishWeatherChangedAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return Results.Ok(ToWeatherProductionDto(settings, moderators));
        });

        static void Apply(NewsFeed feed, SaveNewsFeedDto request)
        {
            feed.Label = request.Label.Trim();
            feed.Url = request.Url.Trim();
            feed.Language = StationLanguages.Normalize(request.Language);
            feed.Region = string.IsNullOrWhiteSpace(request.Region) ? "global" : request.Region.Trim().ToLowerInvariant();
            feed.Category = string.IsNullOrWhiteSpace(request.Category) ? "general" : request.Category.Trim().ToLowerInvariant();
            feed.IsEnabled = request.IsEnabled;
            feed.PollCadenceMinutes = Math.Clamp(request.PollCadenceMinutes, 5, 24 * 60);
            feed.MaxItemsPerPoll = Math.Clamp(request.MaxItemsPerPoll, 1, 100);
        }
    }

    private static void MapBranding(RouteGroupBuilder api)
    {
        api.MapGet("/branding", async (RadioDbContext db, CancellationToken ct) =>
        {
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            var jingles = await db.Jingles.AsNoTracking()
                .OrderBy(jingle => jingle.Label)
                .ThenByDescending(jingle => jingle.CreatedAtUtc)
                .ToListAsync(ct);

            return Results.Ok(ToBrandingDto(settings, jingles));
        });

        api.MapPut("/branding", async (
            SaveBrandingDto request,
            RadioDbContext db,
            CancellationToken ct) =>
        {
            var settings = await db.StationSettings.FindStationSettingsAsync(ct);
            if (settings is null)
            {
                settings = new StationSettings { Id = StationSettings.SingletonId };
                db.StationSettings.Add(settings);
            }

            settings.StationName = string.IsNullOrWhiteSpace(request.StationName)
                ? settings.StationName
                : request.StationName.Trim();
            settings.StationSlogan = SanitizeOptional(request.StationSlogan, settings.StationSlogan);
            settings.StationVision = SanitizeOptional(request.StationVision, settings.StationVision);
            settings.StationMission = SanitizeOptional(request.StationMission, settings.StationMission);

            await db.SaveChangesAsync(ct);

            var jingles = await db.Jingles.AsNoTracking()
                .OrderBy(jingle => jingle.Label)
                .ThenByDescending(jingle => jingle.CreatedAtUtc)
                .ToListAsync(ct);

            return Results.Ok(ToBrandingDto(settings, jingles));
        });

        api.MapGet("/jingles", async (RadioDbContext db, CancellationToken ct) =>
        {
            var jingles = await db.Jingles.AsNoTracking()
                .OrderBy(jingle => jingle.Label)
                .ThenByDescending(jingle => jingle.CreatedAtUtc)
                .ToListAsync(ct);
            return Results.Ok(jingles.Select(ToDto).ToList());
        });

        api.MapPost("/jingles", async (
            CreateJingleDto request,
            JingleProductionService production,
            IHubContext<RadioHub> hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Label))
            {
                return Results.BadRequest("Label is required.");
            }

            try
            {
                var jingle = await production.GenerateAsync(request, ct);
                await hub.Clients.All.SendAsync("JinglesChanged", ct);
                return Results.Ok(ToDto(jingle));
            }
            catch (MusicBackendUnavailableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (MusicProviderValidationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (MusicGenerationFailedException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: 504);
            }
        });

        api.MapGet("/jingles/{id:guid}/audio", async (
            Guid id,
            RadioDbContext db,
            IOptions<RadioOptions> radio,
            CancellationToken ct) =>
        {
            var jingle = await db.Jingles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
            if (jingle is null)
            {
                return Results.NotFound();
            }

            var path = Path.Combine(radio.Value.DataRoot, jingle.FilePath);
            return File.Exists(path)
                ? Results.File(path, "audio/wav", enableRangeProcessing: true)
                : Results.NotFound();
        });

        api.MapPost("/jingles/{id:guid}/toggle", async (
            Guid id,
            RadioDbContext db,
            IHubContext<RadioHub> hub,
            CancellationToken ct) =>
        {
            var jingle = await db.Jingles.FirstOrDefaultAsync(item => item.Id == id, ct);
            if (jingle is null)
            {
                return Results.NotFound();
            }

            jingle.IsActive = !jingle.IsActive;
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("JinglesChanged", ct);
            return Results.Ok(ToDto(jingle));
        });

        api.MapDelete("/jingles/{id:guid}", async (
            Guid id,
            RadioDbContext db,
            IHubContext<RadioHub> hub,
            IOptions<RadioOptions> radio,
            CancellationToken ct) =>
        {
            var jingle = await db.Jingles.FirstOrDefaultAsync(item => item.Id == id, ct);
            if (jingle is null)
            {
                return Results.NotFound();
            }

            db.Jingles.Remove(jingle);
            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("JinglesChanged", ct);

            try
            {
                var path = Path.Combine(radio.Value.DataRoot, jingle.FilePath);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // DB row is gone; a stray WAV can be cleaned by storage maintenance.
            }

            return Results.NoContent();
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
                NextOnAir(slots.Where(s => s.FormatId == f.Id), now), f.Talkativeness,
                f.TalkDepth.ToString(), f.TalkDensity)).ToList());
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
                    request.Description, request.Gender, request.Language,
                    BuildVoiceIntroSample(request.Name, request.Language), ct);
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
                var voice = await designer.DesignVoiceAsync(
                    description, moderator.Gender, moderator.Language,
                    BuildVoiceIntroSample(moderator.Name, moderator.Language), ct);
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

    /// <summary>The preview introduces the host by name — what you hear is what
    /// goes on air, including how the voice pronounces its own name.</summary>
    private static string? BuildVoiceIntroSample(string? name, string language)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : $"Hi, I'm {name.Trim()}! You're listening to WhipRadio — where every song is made just for you. Stay tuned!";

    private static string BuildModeratorVoiceDescription(
        string name,
        string gender,
        string style,
        string persona)
    {
        var genderWord = gender == ModeratorGenders.Male ? "male" : "female";
        var description = $"A {genderWord} English radio host voice. Style: {style}. {persona}";
        return description.Length <= 500 ? description : description[..500];
    }

    private static void MapMixer(RouteGroupBuilder api)
    {
        api.MapGet("/mixer", async (MixerOverviewService overview, CancellationToken ct)
            => Results.Ok(await overview.GetAsync(ct)));

        api.MapPut("/mixer/settings", async (
            MixerSettingsDto request,
            RadioDbContext db,
            IMixerUpdatePublisher mixerUpdates,
            CancellationToken ct) =>
        {
            if (!WhipRadio.Core.Audio.MixPlanner.TryValidateWeightsJson(request.StrategyWeightsJson, out var error))
            {
                return Results.BadRequest($"Strategy weights: {error}");
            }

            var s = await db.StationSettings.FindStationSettingsAsync(ct);
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
            await mixerUpdates.PublishAsync(ct);
            return Results.Ok();
        });

        // "Re-run backfill": drop stub rows (failed analyses) so the backfill
        // service picks them up on its next cycle.
        api.MapPost("/mixer/backfill", async (
            RadioDbContext db,
            IMixerUpdatePublisher mixerUpdates,
            CancellationToken ct) =>
        {
            var removed = await db.MediaAnalyses.Where(a => a.AnalyzerVersion == 0).ExecuteDeleteAsync(ct);
            await mixerUpdates.PublishAsync(ct);
            return Results.Ok(new { removedStubs = removed });
        });
    }

    private static void MapStudios(RouteGroupBuilder api)
    {
        api.MapGet("/studios", async (RadioDbContext db, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var studios = await db.Studios.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct);
            var jobs = coordinator.ActiveJobs;
            var snapshots = await Task.WhenAll(studios.Select(async s =>
            {
                var job = jobs.TryGetValue(s.Id, out var j) ? j : null;
                var runtime = await coordinator.GetRuntimeStateAsync(s, job, ct);
                return ToStudioDto(s, job, runtime);
            }));
            return Results.Ok(snapshots.ToList());
        });

        api.MapPost("/studios/test", async (TestStudioDto request, StudioCoordinator coordinator, CancellationToken ct) =>
        {
            var (ok, provider, detail) = await coordinator.TestAsync(
                ParseStudioKind(request.Kind), request.Source, request.Url, request.Provider, request.ApiKey, ct);
            return Results.Ok(new StudioTestResultDto(ok, provider, detail));
        });

        api.MapPost("/studios", async (SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
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
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPut("/studios/{id:guid}", async (Guid id, SaveStudioDto request, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
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
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapPost("/studios/{id:guid}/toggle", async (Guid id, RadioDbContext db,
            IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var studio = await db.Studios.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            studio.IsActive = !studio.IsActive;
            await db.SaveChangesAsync(ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(ToStudioDto(studio, null));
        });

        api.MapDelete("/studios/{id:guid}", async (Guid id, RadioDbContext db,
            StudioCoordinator coordinator, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            if (coordinator.ActiveJobs.ContainsKey(id))
            {
                return Results.Conflict("Studio is recording right now — wait for the job to finish.");
            }

            var deleted = await db.Studios.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                await updatePublisher.PublishStudiosChangedAsync(ct);
            }

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/studios/{id:guid}/restart", async (Guid id, RadioDbContext db,
            StudioDockerControl dockerControl, IStudioUpdatePublisher updatePublisher, CancellationToken ct) =>
        {
            var studio = await db.Studios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (studio is null)
            {
                return Results.NotFound();
            }

            var (ok, detail) = await dockerControl.TryRestartAsync(
                studio, "manual restart from studios page", force: true, ct);
            await updatePublisher.PublishStudiosChangedAsync(ct);
            return Results.Ok(new StudioRestartResultDto(ok, detail));
        });
    }

    private static void MapStudioHistory(RouteGroupBuilder api)
    {
        api.MapGet("/studio-history", async (
            Guid? studioId,
            string? kind,
            string? status,
            string? search,
            int? page,
            int? pageSize,
            RadioDbContext db,
            StudioCoordinator studios,
            CancellationToken ct) =>
        {
            var query = db.StudioHistory.AsNoTracking();
            if (studioId is { } id)
            {
                query = query.Where(h => h.StudioId == id);
            }

            if (!string.IsNullOrWhiteSpace(kind)
                && Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsedKind))
            {
                query = query.Where(h => h.StudioKind == parsedKind);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var trimmedStatus = status.Trim();
                query = query.Where(h => h.Status == trimmedStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(h =>
                    h.StudioName.Contains(term)
                    || h.Provider.Contains(term)
                    || h.Operation.Contains(term)
                    || h.Prompt.Contains(term)
                    || (h.Result != null && h.Result.Contains(term))
                    || (h.Detail != null && h.Detail.Contains(term))
                    || (h.Error != null && h.Error.Contains(term)));
            }

            var pageNumber = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 10, 100);
            var syntheticRows = await BuildActiveStudioHistoryRowsAsync(
                studios, db, studioId, kind, status, search, ct);
            var total = await query.CountAsync(ct) + syntheticRows.Count;
            var take = (pageNumber * size) + syntheticRows.Count;
            var entries = await query
                .OrderByDescending(h => h.StartedAtUtc)
                .Take(take)
                .ToListAsync(ct);
            var pageRows = entries
                .Select(ToStudioHistoryDto)
                .Concat(syntheticRows)
                .OrderByDescending(h => h.StartedAtUtc)
                .ThenBy(h => h.Operation)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToList();

            return Results.Ok(new PagedStudioHistoryDto(total, pageRows));
        });
    }

    private static async Task<List<StudioHistoryEntryDto>> BuildActiveStudioHistoryRowsAsync(
        StudioCoordinator coordinator,
        RadioDbContext db,
        Guid? studioId,
        string? kind,
        string? status,
        string? search,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(status)
            && !string.Equals(status.Trim(), StudioHistoryStatus.Running, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var activeJobs = coordinator.ActiveJobs.ToArray();
        if (activeJobs.Length == 0)
        {
            return [];
        }

        var activeIds = activeJobs.Select(job => job.Key).ToList();
        var persistedRunningIds = await db.StudioHistory
            .AsNoTracking()
            .Where(h => h.Status == StudioHistoryStatus.Running
                && h.StudioId != null
                && activeIds.Contains(h.StudioId.Value))
            .Select(h => h.StudioId!.Value)
            .ToListAsync(ct);
        var persistedRunningIdSet = persistedRunningIds.ToHashSet();
        var activeStudios = await db.Studios
            .AsNoTracking()
            .Where(s => activeIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var rows = new List<StudioHistoryEntryDto>();
        foreach (var (id, job) in activeJobs)
        {
            if (persistedRunningIdSet.Contains(id) || !activeStudios.TryGetValue(id, out var studio))
            {
                continue;
            }

            if (studioId is { } filteredStudioId && filteredStudioId != id)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(kind)
                && Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsedKind)
                && studio.Kind != parsedKind)
            {
                continue;
            }

            var prompt = $"Running studio job: {job.Label}";
            var detail = "Live job from the Studios overview. Prompt/result will appear when the studio writes a history row.";
            if (!MatchesHistorySearch(studio, job, prompt, detail, search))
            {
                continue;
            }

            var duration = Math.Max(0, (DateTime.UtcNow - job.StartedAtUtc).TotalSeconds);
            rows.Add(new StudioHistoryEntryDto(
                CreateActiveHistoryId(id, job.StartedAtUtc),
                id,
                studio.Name,
                studio.Kind.ToString(),
                studio.Provider,
                job.Label,
                StudioHistoryStatus.Running,
                job.StartedAtUtc,
                null,
                duration,
                Preview(prompt),
                null,
                prompt,
                null,
                detail,
                null));
        }

        return rows;
    }

    private static bool MatchesHistorySearch(
        Studio studio,
        StudioJob job,
        string prompt,
        string detail,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();
        return studio.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || studio.Provider.Contains(term, StringComparison.OrdinalIgnoreCase)
            || job.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || prompt.Contains(term, StringComparison.OrdinalIgnoreCase)
            || detail.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid CreateActiveHistoryId(Guid studioId, DateTime startedAtUtc)
    {
        var input = Encoding.UTF8.GetBytes($"{studioId:N}:{startedAtUtc.Ticks}");
        var bytes = MD5.HashData(input);
        return new Guid(bytes);
    }

    private static StudioKind ParseStudioKind(string kind)
        => Enum.TryParse<StudioKind>(kind, ignoreCase: true, out var parsed) ? parsed : StudioKind.Recording;

    private static string DefaultStudioName(StudioKind kind, int number)
        => kind switch
        {
            StudioKind.WriterRoom => $"Writer Room #{number}",
            StudioKind.VoiceBooth => $"Booth #{number}",
            _ => $"Studio #{number}",
        };

    private static StudioDto ToStudioDto(Studio s, StudioJob? job, StudioRuntimeState? runtime = null)
    {
        runtime ??= job is not null
            ? new StudioRuntimeState(StudioRuntimeState.Busy, job.Label)
            : new StudioRuntimeState(s.IsActive ? StudioRuntimeState.Unknown : StudioRuntimeState.Off);

        return new StudioDto(
        s.Id, s.Name, s.Kind.ToString(), s.Url, s.Provider, s.IsActive,
        s.CreatedAt, s.LastUsedAt, s.JobsCompleted, s.JobsFailed,
        job?.Label, job?.StartedAtUtc, job?.Progress, runtime.Status, runtime.Detail);
    }

    private static StudioHistoryEntryDto ToStudioHistoryDto(StudioHistoryEntry entry)
    {
        var end = entry.CompletedAtUtc ?? (entry.Status == StudioHistoryStatus.Running ? DateTime.UtcNow : null);
        var duration = end is null ? null : (double?)(end.Value - entry.StartedAtUtc).TotalSeconds;
        return new StudioHistoryEntryDto(
            entry.Id,
            entry.StudioId,
            entry.StudioName,
            entry.StudioKind.ToString(),
            entry.Provider,
            entry.Operation,
            entry.Status,
            entry.StartedAtUtc,
            entry.CompletedAtUtc,
            duration,
            Preview(entry.Prompt),
            Preview(entry.Result),
            entry.Prompt,
            entry.Result,
            entry.Detail,
            entry.Error);
    }

    private static string Preview(string? text, int max = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : $"{oneLine[..Math.Max(0, max - 3)]}...";
    }

    private static void MapConsole(RouteGroupBuilder api)
    {
        api.MapGet("/console", (InMemoryLogBuffer buffer) =>
            Results.Ok(buffer.Snapshot()
                .Select(e => new ConsoleLineDto(
                    e.TimestampUtc, e.Level, e.Category, e.Message, e.SourceKind, e.SourceName))
                .ToList()));

        api.MapPost("/admin/director/run", (DirectorControl control) =>
        {
            control.TriggerRun();
            return Results.Ok(new { triggered = true, lastRunUtc = control.LastRunUtc });
        });

        api.MapGet("/serverstats", async (ServerStatsCollector collector, CancellationToken ct) =>
            Results.Ok(await collector.CollectAsync(ct)));

        api.MapGet("/server/media-cleanup", (MediaCleanupService cleanup) =>
            Results.Ok(cleanup.CurrentStatus));

        api.MapGet("/server/media-cleanup/preview", async (MediaCleanupService cleanup, CancellationToken ct) =>
            Results.Ok(await cleanup.PlanOrphanLibraryFilesAsync(ct)));

        api.MapPost("/server/media-cleanup", async (MediaCleanupService cleanup, CancellationToken ct) =>
            Results.Accepted(value: await cleanup.StartDeleteOrphanLibraryFilesAsync(ct)));
    }

    private static void MapPrivacy(RouteGroupBuilder api)
    {
        api.MapGet("/privacy", (PrivacyReportService privacy) =>
            Results.Ok(privacy.BuildReport()));
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

    private static ArtistDto ToArtistDto(
        Artist artist,
        int trackCount,
        int upVotes,
        int downVotes,
        IReadOnlyList<ArtistMemberDto>? members = null)
        => new(
            artist.Id,
            artist.Name,
            artist.Slug,
            artist.Genre,
            artist.Subgenre,
            artist.StyleDescriptor,
            trackCount,
            upVotes,
            downVotes,
            artist.IsRetired,
            artist.Biography,
            artist.Type,
            artist.Origin,
            artist.FormationYear,
            artist.PromotionText,
            members,
            artist.Language);

    private static TrackDto ToDto(Track t, bool deletionPending = false) => new(
        t.Id, t.Title, t.Genre, t.Subgenre, t.Artist?.Name ?? "—", t.ArtistId, t.HasVocals,
        t.DurationSeconds, t.PlayCount, t.UpVotes, t.DownVotes, t.IsRetired, t.Backend, t.CreatedAt,
        t.Language, t.SongStory, t.Lyrics, t.TargetDurationSeconds, t.Style, deletionPending);

    private static TalkPartDto ToDto(TalkPart part)
        => new(
            part.SortOrder,
            part.Kind.ToString(),
            part.Purpose,
            part.Priority.ToString(),
            part.DesiredDurationSeconds,
            part.WordBudget);

    private static TalkBreakPriority ParseOnDemandPriority(string? value)
        => Enum.TryParse<TalkBreakPriority>(value, ignoreCase: true, out var parsed)
            && parsed == TalkBreakPriority.High
                ? TalkBreakPriority.High
                : TalkBreakPriority.Emergency;

    private static ModeratorDto ToDto(Moderator m, DateTimeOffset localNow) => new(
        m.Id, m.Name, m.Slug, m.Language, m.Gender, m.TtsEngine, m.VoiceId, m.SpeechRate, m.Style,
        m.PersonaPrompt, m.PrefersVocals, m.PreferredGenres, m.IsActive, m.IsAutoGenerated,
        m.Talkativeness, m.IsWeatherSpecialist, m.IsNewsSpecialist, m.PhotoUrl, ToDto(MoodEngine.Baseline(m)), ToDto(MoodEngine.Current(m, localNow)),
        ToDto(HostTalkProfile.FromModerator(m)));

    private static HostTalkProfileDto ToDto(HostTalkProfile profile)
        => new(
            profile.BreakFrequencyTracks,
            profile.MinPartsPerBreak,
            profile.MaxPartsPerBreak,
            string.Join(",", profile.AllowedKinds.OrderBy(kind => kind.ToString())),
            profile.ExactReplayTolerance,
            profile.EvergreenBitTolerance);

    private static async Task<ModeratorUsageDto> BuildModeratorUsageAsync(
        RadioDbContext db,
        int moderatorId,
        CancellationToken ct)
    {
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        return new ModeratorUsageDto(
            settings.NewsPresenterModeratorId == moderatorId,
            settings.WeatherSpecialistModeratorId == moderatorId,
            await db.Formats.AsNoTracking().CountAsync(format => format.ModeratorId == moderatorId, ct),
            await db.TalkBits.AsNoTracking().CountAsync(bit => bit.ModeratorId == moderatorId
                && bit.Status == TalkBitStatus.Active, ct),
            await db.TalkBreaks.AsNoTracking().CountAsync(talkBreak => talkBreak.ModeratorId == moderatorId
                && (talkBreak.Status == TalkBreakStatus.Pending || talkBreak.Status == TalkBreakStatus.Rendered), ct),
            await db.ListenerMessages.AsNoTracking().CountAsync(message => message.ModeratorId == moderatorId
                && (message.Status == ListenerMessageStatus.Pending || message.Status == ListenerMessageStatus.Queued), ct),
            await db.Announcements.AsNoTracking().CountAsync(announcement => announcement.ModeratorId == moderatorId, ct),
            await db.PlayLog.AsNoTracking().CountAsync(entry => entry.ModeratorId == moderatorId, ct));
    }

    private static void ApplyTalkProfile(Moderator moderator, HostTalkProfileDto? profile)
    {
        if (profile is null)
        {
            return;
        }

        moderator.TalkBreakFrequencyTracks = Math.Max(0, profile.BreakFrequencyTracks);
        moderator.MinTalkPartsPerBreak = Math.Clamp(profile.MinPartsPerBreak, 0, 10);
        moderator.MaxTalkPartsPerBreak = Math.Clamp(
            Math.Max(profile.MaxPartsPerBreak, moderator.MinTalkPartsPerBreak),
            1,
            10);
        moderator.AllowedTalkPartKinds = string.IsNullOrWhiteSpace(profile.AllowedTalkPartKinds)
            ? new HostTalkProfileDto().AllowedTalkPartKinds
            : profile.AllowedTalkPartKinds;
        moderator.ExactReplayTolerance = Math.Max(0, profile.ExactReplayTolerance);
        moderator.EvergreenBitTolerance = Math.Clamp(profile.EvergreenBitTolerance, 0, 1);
    }

    private static ModeratorTraitsDto ToDto(HostPersonalityTraits traits)
        => new(
            traits.Energy.ToString(),
            traits.Formality.ToString(),
            traits.HumorLevel.ToString(),
            traits.Talkativeness.ToString(),
            traits.Warmth.ToString());

    private static HostPersonalityTraits ParseBaselineTraits(
        ModeratorTraitsDto? request,
        string style,
        double talkativeness)
    {
        var inferred = MoodEngine.InferBaseline(style, talkativeness);
        if (request is null)
        {
            return inferred;
        }

        return new HostPersonalityTraits(
            ParseTrait(request.Energy, inferred.Energy),
            ParseTrait(request.Formality, inferred.Formality),
            ParseTrait(request.HumorLevel, inferred.HumorLevel),
            ParseTrait(request.Talkativeness, inferred.Talkativeness),
            ParseTrait(request.Warmth, inferred.Warmth));
    }

    private static T ParseTrait<T>(string value, T fallback)
        where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static BrandingDto ToBrandingDto(StationSettings s, IReadOnlyList<Jingle> jingles) => new(
        s.StationName,
        s.StationSlogan,
        s.StationVision,
        s.StationMission,
        jingles.Select(ToDto).ToList());

    private static JingleDto ToDto(Jingle jingle) => new(
        jingle.Id,
        jingle.Label,
        jingle.Prompt,
        jingle.Style,
        jingle.Language,
        jingle.DurationSeconds,
        jingle.Backend,
        jingle.Status.ToString(),
        jingle.IsActive,
        jingle.CreatedAtUtc,
        jingle.LastUsedAtUtc,
        jingle.PlayCount);

    private static StationSettingsDto ToDto(StationSettings s) => new(
        s.StationName, s.StationSlogan, s.StationVision, s.StationMission,
        s.DefaultLanguage, s.TargetQueueLength, s.AnnouncementEveryNTracks,
        s.MusicProductionEnabled, s.PlayoutEnabled, s.MaxLibrarySize,
        s.MinTrackDurationSeconds, s.MaxTrackDurationSeconds, s.EnableBreathMarkers,
        s.FrequencyMhz, s.FirstDayOfWeek, MusicBackends.Normalize(s.DefaultMusicProvider),
        s.TextProvider, s.OpenAiApiKey, s.OpenAiModel,
        s.ElevenLabsEnabled, s.ElevenLabsApiKey, s.GreetingsEnabled,
        s.WeatherEnabled,
        WeatherScheduler.NormalizeCadence(s.WeatherCadenceMinutes),
        s.WeatherSpecialistModeratorId,
        s.WeatherFullHandoverEnabled,
        s.WeatherLocationName,
        s.WeatherLatitude,
        s.WeatherLongitude);

    private static NewsFeedDto ToDto(NewsFeed feed, int itemCount) => new(
        feed.Id,
        feed.Label,
        feed.Url,
        feed.Language,
        feed.Region,
        feed.Category,
        feed.IsEnabled,
        feed.IsSeeded,
        feed.PollCadenceMinutes,
        feed.MaxItemsPerPoll,
        feed.CreatedAtUtc,
        feed.LastPolledAtUtc,
        feed.LastError,
        itemCount);

    private static NewsPackageDto ToDto(NewsPackage package) => new(
        package.Id,
        package.Kind.ToString(),
        package.Status.ToString(),
        package.TargetUtc,
        package.TargetDurationSeconds,
        package.AnnouncementId,
        package.CreatedAtUtc,
        package.ProducedAtUtc,
        package.QueuedAtUtc,
        package.PlayedAtUtc,
        package.FailureReason,
        package.ProductionState,
        package.SourceSummary);

    private static WeatherProductionDto ToWeatherProductionDto(
        StationSettings settings,
        IReadOnlyCollection<Moderator> moderators) => new(
        settings.WeatherEnabled,
        WeatherScheduler.NormalizeCadence(settings.WeatherCadenceMinutes),
        settings.WeatherSpecialistModeratorId,
        settings.WeatherFullHandoverEnabled,
        settings.WeatherLocationName,
        settings.WeatherLatitude,
        settings.WeatherLongitude,
        BuildProductionWarning(settings, moderators));

    private static string? BuildProductionWarning(
        StationSettings settings,
        IReadOnlyCollection<Moderator> moderators)
        => ProductionSpecialistPolicy.BuildWarning(settings, moderators);

    private static bool TryNormalizeFeed(
        SaveNewsFeedDto request,
        out SaveNewsFeedDto normalized,
        out string? error)
    {
        normalized = request;
        error = null;
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            error = "Feed label is required.";
            return false;
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            error = "Feed URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        normalized = request with
        {
            Label = request.Label.Trim(),
            Url = uri.ToString(),
            Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim().ToLowerInvariant(),
            Region = string.IsNullOrWhiteSpace(request.Region) ? "global" : request.Region.Trim().ToLowerInvariant(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? "general" : request.Category.Trim().ToLowerInvariant(),
            PollCadenceMinutes = Math.Clamp(request.PollCadenceMinutes, 5, 24 * 60),
            MaxItemsPerPoll = Math.Clamp(request.MaxItemsPerPoll, 1, 100),
        };
        return true;
    }

    private static string SanitizeOptional(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string HashClient(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private sealed record IcecastStatus([property: JsonPropertyName("icestats")] IceStats? IceStats);

    private sealed record IceStats([property: JsonPropertyName("source")] IcecastSource? Source);

    private sealed record IcecastSource(
        [property: JsonPropertyName("listeners")] int Listeners,
        [property: JsonPropertyName("listener_peak")] int ListenerPeak);
}
