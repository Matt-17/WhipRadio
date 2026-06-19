using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Keeps the record collection stocked — but paced: production runs only while
/// MusicProductionEnabled, only while the library is below MaxLibrarySize, and
/// only while fewer than TargetQueueLength unplayed tracks exist. Every track
/// belongs to a fictional artist whose signature style drives the prompt;
/// disliked artists retire and stop getting new material.
/// </summary>
public class MusicProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    GreetingState greetingState,
    MusicProductionControl control,
    StudioCoordinator studios,
    ProductionGate gate,
    IOptions<RadioOptions> radioOptions,
    IOptions<MusicOptions> musicOptions,
    ILogger<MusicProductionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(musicOptions.Value.ProducerBackoffSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await GetSettingsAsync(stoppingToken);

                // "Create new song" from the library: runs regardless of pacing
                // limits or the production switch — the user asked explicitly.
                if (control.TryPeekManualRequest() is { })
                {
                    if (!await studios.AnyAvailableAsync(StudioKind.Recording, requiredProvider: null, stoppingToken))
                    {
                        logger.LogDebug("Manual music production is queued; no recording studio endpoint is free and ready.");
                    }
                    else if (control.TryDequeueManualRequest() is { } artistId)
                    {
                        try
                        {
                            await ProduceOneTrackAsync(settings, artistId, stoppingToken);
                        }
                        catch (Exception ex) when (IsTransientStudioUnavailable(ex) && !stoppingToken.IsCancellationRequested)
                        {
                            control.RequeueTrackForFront(artistId);
                            logger.LogWarning(ex,
                                "Recording studio became unavailable while producing requested artist {ArtistId}; keeping the request queued.",
                                artistId);
                        }
                    }
                }
                else if (settings.MusicProductionEnabled && await LibraryNeedsTrackAsync(settings, stoppingToken))
                {
                    if (await studios.AnyAvailableAsync(StudioKind.Recording, requiredProvider: null, stoppingToken))
                    {
                        await ProduceOneTrackAsync(settings, forcedArtistId: null, stoppingToken);
                    }
                    else
                    {
                        logger.LogDebug("Automatic music production is waiting; no recording studio endpoint is free and ready.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Music production cycle failed ({Reason}); retrying in {Backoff}s",
                    ex.GetBaseException().Message, backoff.TotalSeconds);
            }

            await Task.Delay(backoff, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private static bool IsTransientStudioUnavailable(Exception ex)
    {
        var baseException = ex.GetBaseException();
        return ex is MusicBackendUnavailableException or HttpRequestException or TaskCanceledException
            || baseException is MusicBackendUnavailableException or HttpRequestException or TaskCanceledException;
    }

    private async Task<StationSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
    }

    private async Task<bool> LibraryNeedsTrackAsync(StationSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var total = await db.Tracks.CountAsync(t => !t.IsRetired, ct);
        if (total >= settings.MaxLibrarySize)
        {
            return false; // shelf is full — don't produce more than anyone can play
        }

        var unplayed = await db.Tracks.CountAsync(t => !t.IsRetired && t.PlayCount == 0, ct);
        return unplayed < settings.TargetQueueLength;
    }

    private async Task ProduceOneTrackAsync(StationSettings settings, Guid? forcedArtistId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();
        var artistCreator = scope.ServiceProvider.GetRequiredService<ArtistCreationService>();

        var context = await schedule.GetCurrentAsync(ct);
        RequestHint? requestHint = null;
        Artist artist;

        if (forcedArtistId is { } forcedId)
        {
            // Library-driven: the track is for THIS artist, in THEIR genre.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            artist = await db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == forcedId, ct)
                ?? throw new InvalidOperationException($"Artist {forcedId} not found for manual production.");
            context = context with { Genre = artist.Genre, Subgenre = artist.Subgenre };
        }
        else
        {
            // A queued listener request steers this cycle; the produced track gets
            // linked back to the message so the host can air it as a dedication.
            requestHint = greetingState.ConsumeRequestHint();
            if (requestHint is not null)
            {
                logger.LogInformation(
                    "Listener request hint: generating {Genre} for message {MessageId}",
                    requestHint.Genre, requestHint.MessageId);
                context = context with
                {
                    Genre = requestHint.Genre,
                    Subgenre = Core.Selection.GenreCatalog.PickSubgenre(requestHint.Genre, Random.Shared),
                };
            }

            artist = await GetOrCreateArtistAsync(artistCreator, context, ct);
        }

        artist = await EnsureArtistBiographyAsync(artist, copywriter, ct);

        var generationToken = control.BeginGeneration(artist.Id, artist.Name, ct);
        var gateHeld = false;
        try
        {
            await gate.WaitAsync(generationToken); // analysis backfill yields while we generate
            gateHeld = true;
            await GenerateAndStoreTrackAsync(settings, context, artist, requestHint, scope, generationToken);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            logger.LogInformation("Cancelled music production for {Artist}", artist.Name);
        }
        finally
        {
            if (gateHeld)
            {
                gate.Release();
            }

            control.EndGeneration();
        }
    }

    private async Task GenerateAndStoreTrackAsync(
        StationSettings settings, ShowContext context, Artist artist, RequestHint? requestHint,
        IServiceScope scope, CancellationToken ct)
    {
        var musicGenerator = scope.ServiceProvider.GetRequiredService<IMusicGenerator>();
        var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();

        var existingTitles = await GetExistingTitlesAsync(ct);
        var history = await GetArtistSongHistoryAsync(artist.Id, ct);
        var minSeconds = Math.Max(30, settings.MinTrackDurationSeconds);
        var maxSeconds = Math.Max(minSeconds, settings.MaxTrackDurationSeconds);
        var supportsVocals = await studios.AnyActiveAsync(StudioKind.Recording, MusicBackends.AceStep, ct);
        var plan = await copywriter.PlanSongAsync(
            artist,
            history,
            existingTitles,
            settings.DefaultLanguage,
            minSeconds,
            maxSeconds,
            supportsVocals,
            ct);
        var plannedDuration = plan.TargetDurationSeconds;
        plan = plan with
        {
            TargetDurationSeconds = SongDurationJitter.Apply(plannedDuration, minSeconds, maxSeconds),
        };
        control.ReportTitle(plan.Title);

        var wantVocals = supportsVocals && plan.WantVocals && !string.IsNullOrWhiteSpace(plan.Lyrics);
        var lyrics = wantVocals ? plan.Lyrics : null;
        var lyricsMode = wantVocals
            ? LyricsMode.Provided
            : LyricsMode.Instrumental;
        var prompt = lyricsMode == LyricsMode.Instrumental
            ? $"{plan.Style}, instrumental"
            : plan.Style;
        var preferredProvider = wantVocals
            ? MusicBackends.AceStep
            : MusicBackends.Normalize(await studios.GetPreferredMusicProviderAsync(ct));
        var artistSongHistory = FormatArtistSongHistoryForBackend(history);

        logger.LogInformation(
            "Generating \"{Title}\" by {Artist} ({Subgenre}, {Duration}s, planned {PlannedDuration}s, language: {Language}, provider: {Provider}, vocals: {Vocals})",
            plan.Title, artist.Name, artist.Subgenre, plan.TargetDurationSeconds, plannedDuration, plan.Language, preferredProvider, wantVocals);

        MusicResult result;
        try
        {
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, plan.TargetDurationSeconds)
                {
                    Provider = preferredProvider,
                    SubGenre = context.Subgenre,
                    Style = plan.Style,
                    LyricsMode = lyricsMode,
                    Language = plan.Language,
                    ArtistName = artist.Name,
                    ArtistBackstory = ArtistGenerationBiography(artist),
                    ArtistStyleDescription = artist.StyleDescriptor,
                    SongTitle = plan.Title,
                    SongStory = plan.Story,
                    ArtistSongHistory = artistSongHistory,
                    AllowProviderFallback = !wantVocals,
                }, ct);
        }
        catch (MusicBackendUnavailableException ex) when (wantVocals)
        {
            logger.LogWarning(ex, "Vocal backend unavailable; falling back to instrumental");
            wantVocals = false;
            lyrics = null;
            prompt = $"{plan.Style}, instrumental";
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, plan.TargetDurationSeconds)
                {
                    Provider = MusicBackends.MusicGen,
                    SubGenre = context.Subgenre,
                    Style = plan.Style,
                    LyricsMode = LyricsMode.Instrumental,
                    Language = plan.Language,
                    ArtistName = artist.Name,
                    ArtistBackstory = ArtistGenerationBiography(artist),
                    ArtistStyleDescription = artist.StyleDescriptor,
                    SongTitle = plan.Title,
                    SongStory = plan.Story,
                    ArtistSongHistory = artistSongHistory,
                    AllowProviderFallback = false,
                }, ct);
        }

        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "tracks", $"{id}.wav");
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, result.WavData, ct);

        var track = new Track
        {
            Id = id,
            Title = plan.Title,
            Genre = context.Genre,
            Subgenre = artist.Subgenre,
            ArtistId = artist.Id,
            Style = plan.Style,
            Language = plan.Language,
            HasVocals = result.BackendUsed == MusicBackends.AceStep && wantVocals,
            Lyrics = result.BackendUsed == MusicBackends.AceStep && wantVocals ? lyrics : null,
            SongStory = plan.Story,
            TargetDurationSeconds = plan.TargetDurationSeconds,
            DurationSeconds = WavFile.GetDurationSeconds(result.WavData),
            FilePath = relativePath,
            GenerationPrompt = BuildStoredGenerationPrompt(plan, artist, history, prompt),
            Backend = result.BackendUsed,
            CreatedAt = DateTime.UtcNow,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Tracks.Add(track);

            // Link the request to its track — the ShowRunner airs it as a dedication.
            if (requestHint is not null)
            {
                await db.ListenerMessages
                    .Where(m => m.Id == requestHint.MessageId && m.Status == ListenerMessageStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.FulfilledByTrackId, track.Id), ct);
            }

            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Added \"{Title}\" by {Artist} to the library ({Duration:F0}s, backend {Backend})",
            plan.Title, artist.Name, track.DurationSeconds, track.Backend);

        // Mixer analysis (BPM, intro/outro, loudness) — failure stores a stub
        // and the backfill retries; the track is playable either way.
        var recorder = scope.ServiceProvider.GetRequiredService<MediaAnalysisRecorder>();
        await recorder.AnalyzeAndStoreAsync(PlayoutItemType.Track, track.Id, relativePath, ct);
    }

    /// <summary>
    /// Reuses an active artist for the current subgenre most of the time; ~25%
    /// of tracks (or when none exists) introduce a brand-new artist.
    /// </summary>
    private async Task<Artist> EnsureArtistBiographyAsync(
        Artist artist, MusicCopywriter copywriter, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(artist.Biography))
        {
            return artist;
        }

        var biography = await copywriter.WriteArtistBiographyAsync(artist, ct);
        artist.Biography = biography;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Artists
            .Where(a => a.Id == artist.Id && (a.Biography == null || a.Biography == ""))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Biography, biography), ct);

        logger.LogInformation("Backfilled biography for artist {Artist}", artist.Name);
        return artist;
    }

    private async Task<Artist> GetOrCreateArtistAsync(ArtistCreationService artistCreator, ShowContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.Artists
            .Where(a => !a.IsRetired && a.Genre == context.Genre)
            .ToListAsync(ct);
        var subgenreMatches = candidates
            .Where(a => string.Equals(a.Subgenre, context.Subgenre, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (subgenreMatches.Count > 0 && Random.Shared.NextDouble() > 0.25)
        {
            return subgenreMatches[Random.Shared.Next(subgenreMatches.Count)];
        }

        var hint = string.IsNullOrWhiteSpace(context.Subgenre)
            ? $"new {context.Genre} artist for the station rotation"
            : $"new {context.Subgenre} artist for the {context.Genre} station rotation";
        var artist = await artistCreator.CreateArtistAsync(hint, context.Genre, context.Subgenre, ct);
        logger.LogInformation("New artist on the roster: {Name} ({Subgenre})", artist.Name, artist.Subgenre);
        return artist;
    }

    private async Task<List<string>> GetExistingTitlesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks.OrderBy(t => t.CreatedAt).Select(t => t.Title).ToListAsync(ct);
    }

    private async Task<List<ArtistSongHistoryItem>> GetArtistSongHistoryAsync(Guid artistId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks
            .AsNoTracking()
            .Where(t => t.ArtistId == artistId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new ArtistSongHistoryItem(
                t.Title,
                t.Style,
                t.Language,
                t.HasVocals,
                t.SongStory,
                t.TargetDurationSeconds,
                t.DurationSeconds,
                t.UpVotes,
                t.DownVotes))
            .ToListAsync(ct);
    }

    private static string FormatArtistSongHistoryForBackend(IReadOnlyCollection<ArtistSongHistoryItem> history)
    {
        if (history.Count == 0)
        {
            return "No previous songs yet.";
        }

        return string.Join(Environment.NewLine, history.TakeLast(12).Select(item =>
        {
            var vocal = item.HasVocals ? "vocal" : "instrumental";
            var duration = item.TargetDurationSeconds ?? (int)Math.Round(item.DurationSeconds);
            var story = string.IsNullOrWhiteSpace(item.SongStory)
                ? ""
                : $" Story: {TrimForStoredPrompt(item.SongStory!, 180)}";

            return $"- {item.Title} ({vocal}, {item.Language}, target {duration}s, likes {item.UpVotes}, dislikes {item.DownVotes}). Style: {TrimForStoredPrompt(item.Style, 160)}.{story}";
        }));
    }

    private static string BuildStoredGenerationPrompt(
        ArtistSongPlan plan,
        Artist artist,
        IReadOnlyCollection<ArtistSongHistoryItem> history,
        string backendPrompt)
    {
        var lines = new List<string>
        {
            $"Title: {plan.Title}",
            $"Language: {plan.Language}",
            $"Vocals: {(plan.WantVocals ? "yes" : "no")}",
            $"Target duration: {plan.TargetDurationSeconds}s",
            $"Style: {plan.Style}",
            $"Story: {plan.Story}",
            $"Artist: {artist.Name}",
            $"Artist style: {artist.StyleDescriptor}",
        };

        if (!string.IsNullOrWhiteSpace(artist.Biography))
        {
            lines.Add($"Artist biography: {artist.Biography}");
        }

        if (!string.IsNullOrWhiteSpace(artist.DeepBackgroundBiography))
        {
            lines.Add($"Artist deep background: {TrimForStoredPrompt(artist.DeepBackgroundBiography, 1200)}");
        }

        lines.Add("Backend prompt:");
        lines.Add(backendPrompt);
        lines.Add("Artist song history:");
        lines.Add(FormatArtistSongHistoryForBackend(history));

        return string.Join(Environment.NewLine, lines);
    }

    private static string TrimForStoredPrompt(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars].TrimEnd() + "...";

    private static string ArtistGenerationBiography(Artist artist)
        => !string.IsNullOrWhiteSpace(artist.DeepBackgroundBiography)
            ? artist.DeepBackgroundBiography
            : artist.Biography ?? artist.StyleDescriptor;
}
