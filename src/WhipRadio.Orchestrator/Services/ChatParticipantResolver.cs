using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Turns a <see cref="ChatParticipantRef"/> into a resolved
/// <see cref="ChatParticipant"/> with display name, permission role, and
/// persona text. Also resolves free-text names ("Ivy Sparks") across hosts,
/// artist members, and guests for invite flows.
/// </summary>
public sealed class ChatParticipantResolver(IDbContextFactory<RadioDbContext> dbFactory)
{
    private const int MaxDeepBackgroundChars = 500;

    public static readonly ChatParticipant Director = new(
        ChatParticipantRef.Director,
        "Program Director",
        CharacterRole.ProgramDirector,
        string.Empty,
        Moderator: null);

    /// <summary>Null reference resolves to the Director; a dangling reference returns null.</summary>
    public async Task<ChatParticipant?> ResolveAsync(ChatParticipantRef? reference, CancellationToken ct)
    {
        if (reference is null || reference.Kind == ChatParticipantKind.Director)
        {
            return Director;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        switch (reference.Kind)
        {
            case ChatParticipantKind.Host when reference.ModeratorId is int moderatorId:
            {
                Moderator? moderator = await db.Moderators.AsNoTracking()
                    .FirstOrDefaultAsync(host => host.Id == moderatorId && host.IsActive, ct);
                return moderator is null ? null : FromModerator(moderator);
            }

            case ChatParticipantKind.ArtistMember when reference.EntityId is Guid memberId:
            {
                ArtistMember? member = await db.ArtistMembers.AsNoTracking()
                    .Include(m => m.Artist)
                    .FirstOrDefaultAsync(m => m.Id == memberId, ct);
                return member is null ? null : FromArtistMember(member);
            }

            case ChatParticipantKind.Guest when reference.EntityId is Guid guestId:
            {
                Guest? guest = await db.Guests.AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == guestId && !g.IsArchived, ct);
                return guest is null ? null : FromGuest(guest);
            }

            default:
                return null;
        }
    }

    /// <summary>Resolves a display name to a participant: hosts first, then artist members, then guests.</summary>
    public async Task<ChatParticipant?> ResolveByNameAsync(string name, CancellationToken ct)
    {
        string needle = name.Trim();
        if (needle.Length == 0)
        {
            return null;
        }

        if (needle.Equals("Director", StringComparison.OrdinalIgnoreCase)
            || needle.Equals("Program Director", StringComparison.OrdinalIgnoreCase))
        {
            return Director;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        string lowered = needle.ToLowerInvariant();

        Moderator? moderator = await db.Moderators.AsNoTracking()
            .Where(host => host.IsActive)
            .OrderBy(host => host.Name)
            .FirstOrDefaultAsync(host => host.Name.ToLower() == lowered, ct);
        if (moderator is not null)
        {
            return FromModerator(moderator);
        }

        ArtistMember? member = await db.ArtistMembers.AsNoTracking()
            .Include(m => m.Artist)
            .Where(m => m.Artist != null && !m.Artist.IsRetired)
            .OrderBy(m => m.Name)
            .FirstOrDefaultAsync(m => m.Name.ToLower() == lowered, ct);
        if (member is not null)
        {
            return FromArtistMember(member);
        }

        Guest? guest = await db.Guests.AsNoTracking()
            .Where(g => !g.IsArchived)
            .OrderBy(g => g.Name)
            .FirstOrDefaultAsync(g => g.Name.ToLower() == lowered, ct);
        return guest is null ? null : FromGuest(guest);
    }

    public static ChatParticipant FromModerator(Moderator moderator)
    {
        CharacterRole role = moderator.IsNewsSpecialist
            ? CharacterRole.NewsSpecialist
            : moderator.IsWeatherSpecialist
                ? CharacterRole.WeatherSpecialist
                : CharacterRole.Host;
        return new ChatParticipant(
            ChatParticipantRef.ForHost(moderator.Id),
            moderator.Name,
            role,
            moderator.PersonaPrompt,
            moderator);
    }

    public static ChatParticipant FromArtistMember(ArtistMember member)
        => new(
            ChatParticipantRef.ForArtistMember(member.Id),
            member.Name,
            CharacterRole.Artist,
            BuildMemberPersona(member),
            Moderator: null);

    public static ChatParticipant FromGuest(Guest guest)
        => new(
            ChatParticipantRef.ForGuest(guest.Id),
            guest.Name,
            CharacterRole.Guest,
            BuildGuestPersona(guest),
            Moderator: null);

    private static string BuildMemberPersona(ArtistMember member)
    {
        List<string> parts = [$"{member.Role} of {member.Artist?.Name ?? "an artist"}."];
        AppendPersonaFacts(parts, member.Gender, member.Age, member.Personality, member.Interests);
        parts.Add(member.Biography);
        string background = member.Artist?.DeepBackgroundBiography ?? string.Empty;
        if (background.Length > 0)
        {
            parts.Add(Truncate(background, MaxDeepBackgroundChars));
        }

        return JoinParts(parts);
    }

    private static string BuildGuestPersona(Guest guest)
    {
        List<string> parts = [$"{guest.Expertise}."];
        AppendPersonaFacts(parts, guest.Gender, guest.Age, guest.Personality, guest.Interests);
        parts.Add(guest.Biography);
        if (guest.DeepBackground.Length > 0)
        {
            parts.Add(Truncate(guest.DeepBackground, MaxDeepBackgroundChars));
        }

        return JoinParts(parts);
    }

    private static void AppendPersonaFacts(List<string> parts, string gender, int? age, string personality, string interests)
    {
        List<string> facts = [];
        if (!string.IsNullOrWhiteSpace(gender))
        {
            facts.Add(gender);
        }

        if (age is { } value)
        {
            facts.Add($"{value}");
        }

        if (facts.Count > 0)
        {
            parts.Add($"({string.Join(", ", facts)}).");
        }

        if (!string.IsNullOrWhiteSpace(personality))
        {
            parts.Add($"{personality.TrimEnd('.')}.");
        }

        if (!string.IsNullOrWhiteSpace(interests))
        {
            parts.Add($"Interests: {interests}.");
        }
    }

    private static string JoinParts(IEnumerable<string> parts)
        => string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars].TrimEnd();
}
