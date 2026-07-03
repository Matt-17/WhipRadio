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
}
