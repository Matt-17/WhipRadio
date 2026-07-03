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
}
