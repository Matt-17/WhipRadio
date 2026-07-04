using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Slugs;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public class GuestCreationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    GuestProfileWriter profileWriter,
    GuestCreationQueue creationQueue,
    GuestVoiceQueue voiceQueue,
    ParticipantMemoryWriter participantMemory,
    ILogger<GuestCreationService> logger)
{
    public async Task<Guest> CreateGuestAsync(string? hint, CancellationToken ct = default)
        => await creationQueue.RunAsync(token => CreateGuestCoreAsync(hint, token), ct);

    public async Task<Guest> RedefineGuestAsync(Guid guestId, string? hint, CancellationToken ct = default)
        => await creationQueue.RunAsync(token => RedefineGuestCoreAsync(guestId, hint, token), ct);

    private async Task<Guest> CreateGuestCoreAsync(string? hint, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var existingNames = await CollectPeopleNamesAsync(db, ct);
        var existingSlugs = await db.Guests.AsNoTracking().Select(g => g.Slug).ToListAsync(ct);

        var plan = await profileWriter.DesignGuestAsync(hint, settings, existingNames, ct);
        var name = EnsureUniqueName(plan.Name, existingNames);

        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            Slug = SlugGenerator.UniqueFromName(name, existingSlugs),
        };
        ApplyProfileFields(guest, plan, name, plan.Hint);

        db.Guests.Add(guest);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created guest {Guest} ({Expertise}) from hint '{Hint}'",
            guest.Name,
            guest.Expertise,
            string.IsNullOrWhiteSpace(hint) ? "(none)" : hint.Trim());

        voiceQueue.Enqueue(guest.Id);
        StoreGuestFacts(guest);
        return guest;
    }

    private async Task<Guest> RedefineGuestCoreAsync(Guid guestId, string? hint, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId, ct)
            ?? throw new KeyNotFoundException("Guest was not found.");
        var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var existingNames = (await CollectPeopleNamesAsync(db, ct))
            .Where(name => !string.Equals(name, guest.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var profileHint = BuildRedefinitionHint(guest, hint);
        var plan = await profileWriter.DesignGuestAsync(profileHint, settings, existingNames, ct);
        var storedHint = string.IsNullOrWhiteSpace(hint) ? guest.CreationHint : hint.Trim();

        // Identity stays continuous: keep name and slug, refresh the persona.
        ApplyProfileFields(guest, plan, guest.Name, storedHint);
        guest.VoiceId = null;
        guest.VoiceReferencePath = null;
        guest.VoiceDesignedAtUtc = null;
        guest.VoiceDesignLastError = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Redefined guest {Guest} from hint '{Hint}'",
            guest.Name,
            string.IsNullOrWhiteSpace(hint) ? "(refresh existing profile)" : hint.Trim());

        voiceQueue.EnqueuePriority(guest.Id);
        StoreGuestFacts(guest);
        return guest;
    }

    /// <summary>Seeds retrievable participant memory with guest facts (fire-and-forget).</summary>
    private void StoreGuestFacts(Guest guest)
    {
        List<string> facts = [$"I am {guest.Name}, {guest.Expertise}. {guest.Biography}"];
        if (!string.IsNullOrWhiteSpace(guest.Interests))
        {
            facts.Add($"My interests: {guest.Interests}.");
        }

        participantMemory.StoreFactsAsync(
            ConversationParticipant.GuestKey(guest.Id),
            facts,
            $"guest:{guest.Id}",
            CancellationToken.None).Forget();
    }

    private static void ApplyProfileFields(Guest guest, GuestProfilePlan plan, string name, string? storedHint)
    {
        guest.Name = name;
        guest.Expertise = plan.Expertise;
        guest.Gender = plan.Gender;
        guest.Age = plan.Age;
        guest.Interests = plan.Interests;
        guest.Personality = plan.Personality;
        guest.Biography = plan.Biography;
        guest.DeepBackground = plan.DeepBackground;
        guest.VoiceCreationPrompt = plan.VoiceCreationPrompt;
        guest.CreationHint = storedHint;
        guest.GenerationPrompt = plan.GenerationPrompt;
    }

    /// <summary>Guests must not collide with hosts, artist members, or other guests.</summary>
    private static async Task<List<string>> CollectPeopleNamesAsync(RadioDbContext db, CancellationToken ct)
    {
        var guests = await db.Guests.AsNoTracking().Select(g => g.Name).ToListAsync(ct);
        var hosts = await db.Moderators.AsNoTracking().Select(m => m.Name).ToListAsync(ct);
        var members = await db.ArtistMembers.AsNoTracking().Select(m => m.Name).ToListAsync(ct);
        return guests.Concat(hosts).Concat(members)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();
    }

    private static string BuildRedefinitionHint(Guest guest, string? hint)
    {
        var userHint = string.IsNullOrWhiteSpace(hint)
            ? "Rebuild this guest into a complete, coherent profile. Fill weak or missing persona, background, and voice details."
            : hint.Trim();

        return $"""
            Redefine this existing WhipRadio guest profile.
            The guest identity must stay continuous. Output name exactly as: {guest.Name}
            Preserve useful existing identity but repair weak or outdated fields.

            Current guest:
            Name: {guest.Name}
            Expertise: {guest.Expertise}
            Gender: {guest.Gender}
            Age: {guest.Age}
            Interests: {guest.Interests}
            Personality: {guest.Personality}
            Public biography: {guest.Biography}
            Deep background: {guest.DeepBackground}
            Voice: {guest.VoiceCreationPrompt}

            User redefinition hint:
            {userHint}
            """;
    }

    private static string EnsureUniqueName(string name, IReadOnlyCollection<string> existingNames)
    {
        if (!existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{name} {suffix}";
            if (!existingNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return $"{name} {Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 64)];
    }
}
