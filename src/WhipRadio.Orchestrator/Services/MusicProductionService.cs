using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Persistence;
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
                if (settings.MusicProductionEnabled && await LibraryNeedsTrackAsync(settings, stoppingToken))
                {
                    await ProduceOneTrackAsync(settings, stoppingToken);
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

    private async Task ProduceOneTrackAsync(StationSettings settings, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var musicGenerator = scope.ServiceProvider.GetRequiredService<IMusicGenerator>();
        var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();

        var context = await schedule.GetCurrentAsync(ct);
        var artist = await GetOrCreateArtistAsync(copywriter, context, ct);

        var wantVocals = context.Moderator.PrefersVocals == true
            && await musicGenerator.IsBackendAvailableAsync(MusicBackends.AceStep, ct);
        var lyrics = wantVocals
            ? await copywriter.WriteLyricsAsync(context.Genre, settings.DefaultLanguage, ct)
            : null;

        var existingTitles = await GetExistingTitlesAsync(ct);
        var title = await copywriter.InventTitleAsync(artist, existingTitles, ct);

        var style = artist.StyleDescriptor;
        var prompt = wantVocals ? style : $"{style}, instrumental";
        var minSeconds = Math.Max(30, settings.MinTrackDurationSeconds);
        var maxSeconds = Math.Max(minSeconds, settings.MaxTrackDurationSeconds);
        var duration = Random.Shared.Next(minSeconds, maxSeconds + 1);

        logger.LogInformation(
            "Generating \"{Title}\" by {Artist} ({Subgenre}, {Duration}s, vocals: {Vocals})",
            title, artist.Name, artist.Subgenre, duration, wantVocals);

        MusicResult result;
        try
        {
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, duration), ct);
        }
        catch (MusicBackendUnavailableException ex) when (wantVocals)
        {
            logger.LogWarning(ex, "Vocal backend unavailable; falling back to instrumental");
            wantVocals = false;
            lyrics = null;
            prompt = $"{style}, instrumental";
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, context.Genre, wantVocals, lyrics, duration), ct);
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
            HasVocals = result.BackendUsed == MusicBackends.AceStep,
            Lyrics = lyrics,
            DurationSeconds = WavFile.GetDurationSeconds(result.WavData),
            FilePath = relativePath,
            GenerationPrompt = prompt,
            Backend = result.BackendUsed,
            CreatedAt = DateTime.UtcNow,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Tracks.Add(track);
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
        var (name, styleDescriptor) = await copywriter.InventArtistAsync(
            context.Genre, context.Subgenre, allNames, ct);

        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = name,
            Genre = context.Genre,
            Subgenre = context.Subgenre,
            StyleDescriptor = $"{context.Subgenre}, {styleDescriptor}",
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
