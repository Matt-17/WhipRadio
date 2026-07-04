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
    private static void MapFormatsAndSchedule(RouteGroupBuilder api)
    {
        api.MapGet("/formats", async (RadioDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var formats = await db.Formats.AsNoTracking().Include(f => f.Moderator)
                .OrderByDescending(f => f.IsEnabled).ThenBy(f => f.Name)
                .ToListAsync(ct);
            var slots = await db.ProgramSlots.AsNoTracking().Where(s => s.FormatId != null).ToListAsync(ct);

            var now = time.GetLocalNow();
            return Results.Ok(formats.Select(f => new FormatDto(
                f.Id, f.Name, f.Description, f.Genre, f.Subgenre,
                f.Moderator?.Name, f.ModeratorId, f.Reason, f.IsEnabled, f.UpVotes, f.DownVotes,
                NextOnAir(slots.Where(s => s.FormatId == f.Id), now), f.Talkativeness,
                f.TalkDepth.ToString(), f.TalkDensity)).ToList());
        });

        api.MapPost("/formats/{id:guid}/toggle", async (Guid id, RadioDbContext db, CancellationToken ct) =>
        {
            var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (format is null)
            {
                return Results.NotFound();
            }

            format.IsEnabled = !format.IsEnabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { format.Id, format.IsEnabled });
        });

        api.MapPost("/formats/{id:guid}/vote", async (Guid id, int direction, RadioDbContext db, CancellationToken ct) =>
        {
            var format = await db.Formats.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (format is null)
            {
                return Results.NotFound();
            }

            if (direction > 0)
            {
                format.UpVotes++;
            }
            else
            {
                format.DownVotes++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { format.Id, format.UpVotes, format.DownVotes });
        });

        api.MapGet("/schedule", async (RadioDbContext db, CancellationToken ct) =>
        {
            var slots = await db.ProgramSlots.AsNoTracking()
                .Include(s => s.Format!).ThenInclude(f => f.Moderator)
                .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartMinute)
                .ToListAsync(ct);

            return Results.Ok(slots.Select(s => new ProgramSlotDto(
                s.Id, s.DayOfWeek, s.StartMinute, s.DurationMinutes, s.FormatId,
                s.Format?.Name, s.Format?.Moderator?.Name,
                s.Format is null ? null : string.IsNullOrEmpty(s.Format.Subgenre) ? s.Format.Genre : s.Format.Subgenre,
                s.Format?.SelectionRules.Mode == SelectionMode.NewsShow)).ToList());
        });
    }
}
