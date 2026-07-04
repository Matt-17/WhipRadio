using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    private static void MapGuests(RouteGroupBuilder api)
    {
        api.MapGet("/guests", async (RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var guests = await db.Guests.AsNoTracking()
                .OrderBy(g => g.Name)
                .ToListAsync(ct);
            return Results.Ok(guests.Select(g => ToGuestDto(g, radioOptions.Value.DataRoot)).ToList());
        });

        // Books a new guest from a one-line hint. The profile LLM call runs
        // inside this request (serialized by GuestCreationQueue); the Web page
        // shows queued/creating/failed rows exactly like artist creation.
        api.MapPost("/guests", async (
            CreateGuestRequestDto request,
            GuestCreationService guests,
            IOptions<RadioOptions> radioOptions,
            CancellationToken ct) =>
        {
            try
            {
                var guest = await guests.CreateGuestAsync(request.Hint, ct);
                return Results.Ok(ToGuestDto(guest, radioOptions.Value.DataRoot));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    title: "Guest creation failed",
                    detail: ex.GetBaseException().Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/guests/{id:guid}/redefine", async (
            Guid id,
            RedefineGuestRequestDto request,
            GuestCreationService guests,
            IOptions<RadioOptions> radioOptions,
            CancellationToken ct) =>
        {
            try
            {
                var guest = await guests.RedefineGuestAsync(id, request.Hint, ct);
                return Results.Ok(ToGuestDto(guest, radioOptions.Value.DataRoot));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    title: "Guest redefinition failed",
                    detail: ex.GetBaseException().Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Archives the guest when conversations reference them (history stays
        // readable); hard-deletes otherwise, including the voice reference file.
        api.MapDelete("/guests/{id:guid}", async (
            Guid id, RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (guest is null)
            {
                return Results.NotFound();
            }

            var speakerKey = ConversationParticipant.GuestKey(guest.Id);
            var isReferenced = await db.ConversationSegments.AsNoTracking()
                .AnyAsync(s => s.ParticipantsJson.Contains(speakerKey), ct);
            if (isReferenced)
            {
                guest.IsArchived = true;
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            }

            if (!string.IsNullOrEmpty(guest.VoiceReferencePath))
            {
                var absolutePath = Path.Combine(radioOptions.Value.DataRoot, guest.VoiceReferencePath);
                if (System.IO.File.Exists(absolutePath))
                {
                    System.IO.File.Delete(absolutePath);
                }
            }

            db.Guests.Remove(guest);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Plays the guest's voice reference clip in the footer preview player.
        api.MapGet("/guests/{id:guid}/voice", async (
            Guid id, RadioDbContext db, IOptions<RadioOptions> radioOptions, CancellationToken ct) =>
        {
            var guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
            if (guest is null || string.IsNullOrEmpty(guest.VoiceReferencePath))
            {
                return Results.NotFound();
            }

            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, guest.VoiceReferencePath);
            if (!System.IO.File.Exists(absolutePath))
            {
                return Results.NotFound();
            }

            return Results.File(absolutePath, "audio/wav", enableRangeProcessing: true);
        });

        // (Re)designs the guest's voice on demand; mirrors the artist-member flow.
        api.MapPost("/guests/{id:guid}/voice/recreate", async (
            Guid id, RadioDbContext db, GuestVoiceQueue voiceQueue, CancellationToken ct) =>
        {
            var exists = await db.Guests.AsNoTracking().AnyAsync(g => g.Id == id, ct);
            if (!exists)
            {
                return Results.NotFound();
            }

            await db.Guests
                .Where(g => g.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.VoiceId, (string?)null)
                    .SetProperty(g => g.VoiceReferencePath, (string?)null)
                    .SetProperty(g => g.VoiceDesignedAtUtc, (DateTime?)null)
                    .SetProperty(g => g.VoiceDesignLastError, (string?)null), ct);

            voiceQueue.EnqueuePriority(id);
            return Results.Accepted();
        });
    }

    private static GuestDto ToGuestDto(Guest guest, string dataRoot)
        => new(
            guest.Id,
            guest.Name,
            guest.Slug,
            guest.Expertise,
            guest.Gender,
            guest.Age,
            guest.Interests,
            guest.Personality,
            guest.Biography,
            HasVoice: !string.IsNullOrEmpty(guest.VoiceId),
            HasVoiceReference: !string.IsNullOrEmpty(guest.VoiceReferencePath)
                && System.IO.File.Exists(Path.Combine(dataRoot, guest.VoiceReferencePath)),
            guest.VoiceDesignLastError,
            guest.IsArchived,
            guest.CreatedAtUtc);
}
