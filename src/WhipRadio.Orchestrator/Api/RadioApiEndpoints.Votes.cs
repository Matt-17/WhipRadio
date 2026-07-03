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
    private static void MapVotes(RouteGroupBuilder api)
    {
        api.MapPost("/votes", async (VoteRequestDto request, HttpContext http, RadioDbContext db,
            IHubContext<RadioHub> hub, CancellationToken ct) =>
        {
            // A single toggle moves at most one side by ±1; the client tracks its own
            // current vote, so retracting sends -1 and a switch sends -1/+1.
            if (request.UpDelta is < -1 or > 1 || request.DownDelta is < -1 or > 1
                || (request.UpDelta == 0 && request.DownDelta == 0))
            {
                return Results.BadRequest("Vote deltas must be -1, 0, or +1, with at least one non-zero.");
            }

            var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == request.TrackId, ct);
            if (track is null)
            {
                return Results.NotFound();
            }

            // Clamp at zero so a stale retraction can never drive a tally negative.
            track.UpVotes = Math.Max(0, track.UpVotes + request.UpDelta);
            track.DownVotes = Math.Max(0, track.DownVotes + request.DownDelta);
            track.IsRetired = track.IsRetired || TrackWeighting.ShouldRetire(track);

            var client = HashClient(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await ApplyVoteAuditAsync(db, track.Id, client, request.UpDelta, direction: 1, ct);
            await ApplyVoteAuditAsync(db, track.Id, client, request.DownDelta, direction: -1, ct);

            await db.SaveChangesAsync(ct);

            var result = new VoteResultDto(track.Id, track.UpVotes, track.DownVotes, track.IsRetired);
            await hub.Clients.All.SendAsync("VotesChanged", result, ct);
            return Results.Ok(result);
        });
    }

    // Mirror a ±1 count change in the audit log so the TotalVotes stat stays honest:
    // a fresh vote adds a row, a retraction removes this client's most recent matching
    // row. If no matching row exists (e.g. a different client/IP cast it), the tally
    // still moves — the client remains the source of truth for its own vote.
    private static async Task ApplyVoteAuditAsync(
        RadioDbContext db, Guid trackId, string client, int delta, int direction, CancellationToken ct)
    {
        if (delta > 0)
        {
            db.Votes.Add(new Vote
            {
                TrackId = trackId,
                Direction = direction,
                CreatedAt = DateTime.UtcNow,
                ClientHint = client,
            });
        }
        else if (delta < 0)
        {
            var existing = await db.Votes
                .Where(v => v.TrackId == trackId && v.Direction == direction && v.ClientHint == client)
                .OrderByDescending(v => v.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                db.Votes.Remove(existing);
            }
        }
    }
}
