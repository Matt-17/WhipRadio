using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

public sealed class JingleProductionService(
    IDbContextFactory<RadioDbContext> dbFactory,
    IMusicGenerator musicGenerator,
    IOptions<RadioOptions> radioOptions,
    TimeProvider timeProvider,
    ILogger<JingleProductionService> logger)
{
    public async Task<Jingle> GenerateAsync(CreateJingleDto request, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Station ID" : request.Label.Trim();
        var style = string.IsNullOrWhiteSpace(request.Style)
            ? "warm analog FM, memorable sonic logo, tight drums, bright synth tag"
            : request.Style.Trim();
        var duration = Math.Clamp(request.DurationSeconds, 5, 20);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var prompt = BuildPrompt(settings, label, style, duration);
        var lyrics = BuildLyrics(settings);

        logger.LogInformation("Generating jingle {Label} ({Duration}s) through ACE-Step", label, duration);

        var result = await musicGenerator.GenerateAsync(
            new MusicRequest(prompt, "jingle", WantVocals: true, Lyrics: lyrics, duration)
            {
                Provider = MusicBackends.AceStep,
                SubGenre = "radio identity",
                LyricsMode = LyricsMode.Provided,
                Language = settings.DefaultLanguage,
                VocalStyle = "short sung radio hook",
                ArtistName = settings.StationName,
                AllowProviderFallback = false,
            },
            ct);

        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "jingles", $"{id}.wav");
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, result.WavData, ct);

        var jingle = new Jingle
        {
            Id = id,
            Label = label,
            Prompt = prompt,
            Style = style,
            Language = settings.DefaultLanguage,
            DurationSeconds = WavFile.GetDurationSeconds(result.WavData),
            FilePath = relativePath,
            Backend = result.BackendUsed,
            ModelUsed = result.ModelUsed,
            SeedUsed = result.SeedUsed,
            TaskId = result.TaskId,
            Status = JingleStatus.Ready,
            IsActive = true,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.Jingles.Add(jingle);
        await db.SaveChangesAsync(ct);
        return jingle;
    }

    private static string BuildPrompt(StationSettings settings, string label, string style, int durationSeconds)
    {
        var slogan = string.IsNullOrWhiteSpace(settings.StationSlogan)
            ? settings.StationName
            : settings.StationSlogan.Trim();
        return string.Join(
            " ",
            $"Vocal {durationSeconds}s radio jingle for {Trim(settings.StationName, 40)}.",
            $"Label: {Trim(label, 30)}.",
            $"Mood: {Trim(slogan, 60)}.",
            $"Style: {Trim(style, 80)}.",
            "Sung station ID and slogan hook, sonic logo, clean ending.");
    }

    private static string BuildLyrics(StationSettings settings)
    {
        var stationName = string.IsNullOrWhiteSpace(settings.StationName)
            ? "WhipRadio"
            : settings.StationName.Trim();
        var slogan = string.IsNullOrWhiteSpace(settings.StationSlogan)
            ? stationName
            : settings.StationSlogan.Trim();

        return $"{stationName}\n{slogan}";
    }

    private static string Trim(string value, int maxLength)
    {
        var cleaned = value.Trim();
        return cleaned.Length <= maxLength
            ? cleaned
            : cleaned[..maxLength].TrimEnd(' ', ',', ';', ':') + "...";
    }
}
