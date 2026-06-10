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
/// Keeps the record collection stocked: while the library holds fewer unplayed
/// tracks than StationSettings.TargetQueueLength, it generates one track at a
/// time (genre from the current schedule slot, vocals when the moderator
/// prefers them AND ace-step is available, else instrumental MusicGen).
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
                if (await LibraryNeedsTrackAsync(stoppingToken))
                {
                    await ProduceOneTrackAsync(stoppingToken);
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

    private async Task<bool> LibraryNeedsTrackAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var target = settings?.TargetQueueLength ?? 3;
        var unplayed = await db.Tracks.CountAsync(t => !t.IsRetired && t.PlayCount == 0, ct);
        return unplayed < target;
    }

    private async Task ProduceOneTrackAsync(CancellationToken ct)
    {
        // Scoped resolution: the typed HTTP clients and copywriter are scoped services.
        using var scope = scopeFactory.CreateScope();
        var musicGenerator = scope.ServiceProvider.GetRequiredService<IMusicGenerator>();
        var copywriter = scope.ServiceProvider.GetRequiredService<MusicCopywriter>();

        var (slot, moderator) = await schedule.GetCurrentAsync(ct);
        var genre = slot.Genre;

        string? lyrics = null;
        var wantVocals = moderator.PrefersVocals == true
            && await musicGenerator.IsBackendAvailableAsync(MusicBackends.AceStep, ct);

        string language;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            language = (await db.StationSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.DefaultLanguage ?? "en";
        }

        if (wantVocals)
        {
            lyrics = await copywriter.WriteLyricsAsync(genre, language, ct);
        }

        var title = await copywriter.InventTitleAsync(genre, ct);
        var style = $"{genre}, catchy, radio-friendly, {moderator.Style} mood";
        var prompt = wantVocals ? style : $"{style}, instrumental";
        var duration = musicOptions.Value.TrackDurationSeconds;

        logger.LogInformation("Generating track \"{Title}\" ({Genre}, vocals: {Vocals})", title, genre, wantVocals);

        MusicResult result;
        try
        {
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, genre, wantVocals, lyrics, duration), ct);
        }
        catch (MusicBackendUnavailableException ex) when (wantVocals)
        {
            logger.LogWarning(ex, "Vocal backend unavailable; falling back to instrumental");
            wantVocals = false;
            lyrics = null;
            prompt = $"{style}, instrumental";
            result = await musicGenerator.GenerateAsync(
                new MusicRequest(prompt, genre, wantVocals, lyrics, duration), ct);
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
            Genre = genre,
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
            "Added \"{Title}\" to the library ({Duration:F0}s, backend {Backend})",
            title, track.DurationSeconds, track.Backend);
    }
}
