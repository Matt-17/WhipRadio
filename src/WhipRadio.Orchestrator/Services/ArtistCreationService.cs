using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Slugs;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public class ArtistCreationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    MusicCopywriter copywriter,
    ArtistSocialFeedService socialFeed,
    ArtistCreationQueue creationQueue,
    ArtistMemberVoiceQueue voiceQueue,
    ILogger<ArtistCreationService> logger)
{
    public async Task<Artist> CreateArtistAsync(
        string? hint,
        string? genre = null,
        string? subgenre = null,
        CancellationToken ct = default)
        => await creationQueue.RunAsync(token => CreateArtistCoreAsync(hint, genre, subgenre, token), ct);

    public async Task<Artist> RedefineArtistAsync(
        Guid artistId,
        string? hint,
        CancellationToken ct = default)
        => await creationQueue.RunAsync(token => RedefineArtistCoreAsync(artistId, hint, token), ct);

    private async Task<Artist> CreateArtistCoreAsync(
        string? hint,
        string? genre,
        string? subgenre,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existingNames = await db.Artists.AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => a.Name)
            .ToListAsync(ct);
        var existingSlugs = await db.Artists.AsNoTracking()
            .Select(a => a.Slug)
            .ToListAsync(ct);

        var plan = await copywriter.DesignArtistAsync(hint, genre, subgenre, existingNames, ct);
        var name = EnsureUniqueName(plan.Name, existingNames);
        var now = DateTime.UtcNow;

        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
        };
        ApplyProfileFields(artist, plan, name, plan.Hint);
        artist.Slug = SlugGenerator.UniqueFromName(name, existingSlugs);
        foreach (var member in CreateProfileMembers(plan))
        {
            artist.Members.Add(member);
        }

        db.Artists.Add(artist);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created artist {Artist} from hint '{Hint}' ({Genre}/{Subgenre}, {MemberCount} members)",
            artist.Name,
            string.IsNullOrWhiteSpace(hint) ? "(none)" : hint.Trim(),
            artist.Genre,
            artist.Subgenre,
            artist.Members.Count);

        await socialFeed.TryCreateArtistCreatedPostAsync(artist.Id, ct);
        voiceQueue.EnqueueMany(artist.Members
            .Where(member => string.IsNullOrWhiteSpace(member.VoiceId))
            .Select(member => member.Id));

        return artist;
    }

    private async Task<Artist> RedefineArtistCoreAsync(Guid artistId, string? hint, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var artist = await db.Artists
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);
        if (artist is null)
        {
            throw new KeyNotFoundException("Artist was not found.");
        }

        var currentMembers = await db.ArtistMembers.AsNoTracking()
            .Where(m => m.ArtistId == artistId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        var existingNames = await db.Artists.AsNoTracking()
            .Where(a => a.Id != artistId)
            .OrderBy(a => a.Name)
            .Select(a => a.Name)
            .ToListAsync(ct);

        var profileHint = BuildRedefinitionHint(artist, currentMembers, hint);
        var plan = await copywriter.DesignArtistAsync(profileHint, artist.Genre, artist.Subgenre, existingNames, ct);
        var name = artist.Name;
        var oldName = artist.Name;
        var storedHint = string.IsNullOrWhiteSpace(hint)
            ? artist.CreationHint
            : hint.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ApplyProfileFields(artist, plan, name, storedHint);
        await db.ArtistMembers
            .Where(m => m.ArtistId == artistId)
            .ExecuteDeleteAsync(ct);
        var newMembers = CreateProfileMembers(plan, artist.Id);
        db.ArtistMembers.AddRange(newMembers);
        artist.Members = newMembers;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Redefined artist {OldArtist} as {Artist} from hint '{Hint}' ({Genre}/{Subgenre}, {MemberCount} members)",
            oldName,
            artist.Name,
            string.IsNullOrWhiteSpace(hint) ? "(refresh existing profile)" : hint.Trim(),
            artist.Genre,
            artist.Subgenre,
            artist.Members.Count);
        voiceQueue.EnqueueMany(newMembers
            .Where(member => string.IsNullOrWhiteSpace(member.VoiceId))
            .Select(member => member.Id));

        return artist;
    }

    private static void ApplyProfileFields(
        Artist artist,
        ArtistProfilePlan plan,
        string name,
        string? storedHint)
    {
        artist.Name = name;
        artist.Genre = plan.Genre;
        artist.Subgenre = plan.Subgenre;
        artist.StyleDescriptor = plan.Style;
        artist.Type = plan.Type;
        artist.Origin = plan.Origin;
        artist.Language = plan.Language;
        artist.FormationYear = plan.FormationYear;
        artist.CreationHint = storedHint;
        artist.Biography = plan.ShortBiography;
        artist.DeepBackgroundBiography = plan.DeepBackgroundBiography;
        artist.PromotionText = plan.PromotionText;
        artist.GenerationPrompt = plan.GenerationPrompt;
    }

    private static List<ArtistMember> CreateProfileMembers(ArtistProfilePlan plan, Guid? artistId = null)
    {
        var members = new List<ArtistMember>();
        var order = 0;
        foreach (var member in plan.Members)
        {
            var artistMember = new ArtistMember
            {
                Id = Guid.NewGuid(),
                SortOrder = order++,
                Name = member.Name,
                Role = member.Role,
                Biography = member.Biography,
                VoiceCreationPrompt = member.VoiceCreationPrompt,
            };
            if (artistId is { } id)
            {
                artistMember.ArtistId = id;
            }

            members.Add(artistMember);
        }

        return members;
    }

    private static string BuildRedefinitionHint(Artist artist, IEnumerable<ArtistMember> members, string? hint)
    {
        var userHint = string.IsNullOrWhiteSpace(hint)
            ? "Rebuild this artist into a complete, coherent rich profile. Fill missing member, language, biography, story, and voice details."
            : hint.Trim();

        return $"""
            Redefine this existing WhipRadio artist profile.
            The artist identity must stay continuous. Output Name exactly as: {artist.Name}
            Preserve useful existing identity, genre lane, and song-language intent, but repair weak or outdated fields.

            Current artist:
            Name: {artist.Name}
            Type: {artist.Type}
            Genre: {artist.Genre}
            Subgenre: {artist.Subgenre}
            Origin: {artist.Origin}
            Formation year: {artist.FormationYear}
            Canonical song language: {artist.Language}
            Style: {artist.StyleDescriptor}
            Public biography: {artist.Biography}
            Deep background: {artist.DeepBackgroundBiography}
            Promotion text: {artist.PromotionText}
            Current members:
            {FormatMembers(members)}

            User redefinition hint:
            {userHint}
            """;
    }

    private static string FormatMembers(IEnumerable<ArtistMember> members)
    {
        var lines = members
            .OrderBy(m => m.SortOrder)
            .Select(m => $"- {m.Name}: {m.Role}. Bio: {m.Biography}. Voice: {m.VoiceCreationPrompt}")
            .ToList();

        return lines.Count == 0 ? "(none recorded)" : string.Join(Environment.NewLine, lines);
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
