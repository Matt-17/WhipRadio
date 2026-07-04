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
    private static void MapProduction(RouteGroupBuilder api)
    {
        api.MapGet("/production/news", async (
            RadioDbContext db,
            TimeProvider timeProvider,
            IEnumerable<ITopOfHourSegmentContributor> contributors,
            CancellationToken ct) =>
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
            var packageAnnouncementIds = packages
                .Where(package => package.AnnouncementId is not null)
                .Select(package => package.AnnouncementId!.Value)
                .ToList();
            var packageTranscripts = packageAnnouncementIds.Count == 0
                ? new Dictionary<Guid, string?>()
                : await db.Announcements.AsNoTracking()
                    .Where(a => packageAnnouncementIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, a => TranscriptOf(a), ct);
            var nextPlan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, timeProvider.GetLocalNow(), contributors);
            var nextTargetUtc = nextPlan.TargetLocal.UtcDateTime;
            var nextPackageStatus = await db.NewsPackages.AsNoTracking()
                .Where(package => package.TargetUtc == nextTargetUtc
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
                packages.Select(package => ToDto(
                    package,
                    package.AnnouncementId is { } announcementId
                        ? packageTranscripts.GetValueOrDefault(announcementId)
                        : null)).ToList(),
                settings.NewsLongFormatEnabled,
                LongFormatNewsScheduler.FormatAirTimes(
                    LongFormatNewsScheduler.ParseAirTimes(settings.NewsLongFormatAirTimes)),
                LongFormatNewsScheduler.NormalizeDurationMinutes(settings.NewsLongFormatDurationMinutes)));
        });

        api.MapPut("/production/news/settings", async (
            SaveNewsProductionSettingsDto request,
            RadioDbContext db,
            IProductionUpdatePublisher productionUpdates,
            NewsShowScheduleSeeder scheduleSeeder,
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

            var airTimes = LongFormatNewsScheduler.ParseAirTimes(request.NewsLongFormatAirTimes);
            if (request.NewsLongFormatEnabled && airTimes.Count == 0)
            {
                return Results.BadRequest("Long news format needs at least one valid HH:mm air time.");
            }

            settings.NewsLongFormatEnabled = request.NewsLongFormatEnabled;
            settings.NewsLongFormatAirTimes = LongFormatNewsScheduler.FormatAirTimes(airTimes);
            settings.NewsLongFormatDurationMinutes =
                LongFormatNewsScheduler.NormalizeDurationMinutes(request.NewsLongFormatDurationMinutes);

            await db.SaveChangesAsync(ct);
            await scheduleSeeder.SyncAsync(ct);
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
}
