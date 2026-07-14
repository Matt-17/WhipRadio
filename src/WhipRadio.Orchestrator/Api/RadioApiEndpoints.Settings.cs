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
            settings.ArchiveUploadEnabled = request.ArchiveUploadEnabled;
            settings.ArchivePlayoutEnabled = request.ArchivePlayoutEnabled;
            settings.ArchiveEnrichmentEnabled = request.ArchiveEnrichmentEnabled;
            settings.PodcastKnowledgeEnabled = request.PodcastKnowledgeEnabled;

            await db.SaveChangesAsync(ct);

            // Language changed → every host follows the station language.
            if (!string.Equals(previousLanguage, settings.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                await aligner.AlignAsync(ct);
            }

            return Results.Ok(ToDto(settings));
        });
    }
}
