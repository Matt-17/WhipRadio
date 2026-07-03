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
}
