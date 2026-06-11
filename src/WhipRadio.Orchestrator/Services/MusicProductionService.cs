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
                if (control.TryDequeueManualRequest() is { } artistId)
                {
                    await ProduceOneTrackAsync(settings, artistId, stoppingToken);
                }
                else if (settings.MusicProductionEnabled && await LibraryNeedsTrackAsync(settings, stoppingToken))
                {
                    await ProduceOneTrackAsync(settings, forcedArtistId: null, stoppingToken);
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

    private async Task<StationSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new StationSettings();
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

            artist = await GetOrCreateArtistAsync(copywriter, context, ct);
        }

        control.BeginGeneration(artist.Id, artist.Name);
        try
        {
            await GenerateAndStoreTrackAsync(settings, context, artist, requestHint, scope, ct);
        }
        finally
        {
            control.EndGeneration();
        }
    }

    private async Task GenerateAndStoreTrackAsync(
        StationSettings settings, ShowContext context, Artist artist, RequestHint? requestHint,
        IServiceScope scope, CancellationToken ct)
    {
        var musicGenerator = scope.ServiceProvider.GetRequiredService<IMusicGenerator>();
        var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();

        // The first active recording studio sets the tone: its protocol decides
        // whether vocals are even possible for this production cycle.
        var provider = MusicBackends.Normalize(await studios.GetPreferredMusicProviderAsync(ct));
        var wantVocals = provider != MusicBackends.MusicGen && context.Moderator.PrefersVocals == true;
        var lyrics = wantVocals
            ? await copywriter.WriteLyricsAsync(context.Genre, settings.DefaultLanguage, ct)
            : null;

        var existingTitles = await GetExistingTitlesAsync(ct);
        var title = await copywriter.InventTitleAsync(artist, existingTitles, ct);
        control.ReportTitle(title);

        var style = artist.StyleDescriptor;
        var minSeconds = Math.Max(30, settings.MinTrackDurationSeconds);
        var maxSeconds = Math.Max(minSeconds, settings.MaxTrackDurationSeconds);
        var duration = Random.Shared.Next(minSeconds, maxSeconds + 1);
        var lyricsMode = wantVocals
            ? LyricsMode.Provided
            : LyricsMode.Instrumental;
        var prompt = provider == MusicBackends.MusicGen || lyricsMode == LyricsMode.Instrumental
            ? $"{style}, instrumental"
            : style;

        logger.LogInformation(
            "Generating \"{Title}\" by {Artist} ({Subgenre}, {Duration}s, provider: {Provider}, vocals: {Vocals})",
            title, artist.Name, artist.Subgenre, duration, provider, wantVocals);

        MusicResult result;
        try
        {
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, duration)
                {
                    Provider = provider,
                    SubGenre = context.Subgenre,
                    Style = style,
                    LyricsMode = lyricsMode,
                    Language = settings.DefaultLanguage,
                    ArtistName = artist.Name,
                    ArtistBackstory = artist.Biography ?? artist.StyleDescriptor,
                    ArtistStyleDescription = style,
                    AllowProviderFallback = true,
                }, ct);
        }
        catch (MusicBackendUnavailableException ex) when (wantVocals)
        {
            logger.LogWarning(ex, "Vocal backend unavailable; falling back to instrumental");
            wantVocals = false;
            lyrics = null;
            prompt = $"{style}, instrumental";
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, duration)
                {
                    Provider = MusicBackends.MusicGen,
                    SubGenre = context.Subgenre,
                    Style = style,
                    LyricsMode = LyricsMode.Instrumental,
                    Language = settings.DefaultLanguage,
                    ArtistName = artist.Name,
                    ArtistBackstory = artist.Biography ?? artist.StyleDescriptor,
                    ArtistStyleDescription = style,
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
            Title = title,
            Genre = context.Genre,
            Subgenre = artist.Subgenre,
            ArtistId = artist.Id,
            Style = style,
            HasVocals = result.BackendUsed == MusicBackends.AceStep && wantVocals,
            Lyrics = result.BackendUsed == MusicBackends.AceStep && wantVocals ? lyrics : null,
            DurationSeconds = WavFile.GetDurationSeconds(result.WavData),
            FilePath = relativePath,
            GenerationPrompt = prompt,
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
            title, artist.Name, track.DurationSeconds, track.Backend);
    }

    /// <summary>
    /// Reuses an active artist for the current subgenre most of the time; ~25%
    /// of tracks (or when none exists) introduce a brand-new artist.
    /// </summary>
    private async Task<Artist> GetOrCreateArtistAsync(MusicCopywriter copywriter, ShowContext context, CancellationToken ct)
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

        var allNames = await db.Artists.Select(a => a.Name).ToListAsync(ct);
        var (name, styleDescriptor, biography) = await copywriter.InventArtistAsync(
            context.Genre, context.Subgenre, allNames, ct);

        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = name,
            Genre = context.Genre,
            Subgenre = context.Subgenre,
            StyleDescriptor = $"{context.Subgenre}, {styleDescriptor}",
            Biography = biography,
            CreatedAt = DateTime.UtcNow,
        };

        db.Artists.Add(artist);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("New artist on the roster: {Name} ({Subgenre})", name, artist.Subgenre);
        return artist;
    }

    private async Task<List<string>> GetExistingTitlesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks.OrderBy(t => t.CreatedAt).Select(t => t.Title).ToListAsync(ct);
    }
}
