using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public class ArtistCreationService(
    IDbContextFactory<RadioDbContext> dbFactory,
    MusicCopywriter copywriter,
    ArtistCreationQueue creationQueue,
    ILogger<ArtistCreationService> logger)
{
    public async Task<Artist> CreateArtistAsync(
        string? hint,
        string? genre = null,
        string? subgenre = null,
        CancellationToken ct = default)
        => await creationQueue.RunAsync(token => CreateArtistCoreAsync(hint, genre, subgenre, token), ct);

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

        var plan = await copywriter.DesignArtistAsync(hint, genre, subgenre, existingNames, ct);
        var name = EnsureUniqueName(plan.Name, existingNames);
        var now = DateTime.UtcNow;

        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = name,
            Genre = plan.Genre,
            Subgenre = plan.Subgenre,
            StyleDescriptor = plan.Style,
            Type = plan.Type,
            Origin = plan.Origin,
            FormationYear = plan.FormationYear,
            CreationHint = plan.Hint,
            Biography = plan.ShortBiography,
            DeepBackgroundBiography = plan.DeepBackgroundBiography,
            PromotionText = plan.PromotionText,
            GenerationPrompt = plan.GenerationPrompt,
            CreatedAt = now,
        };

        var order = 0;
        foreach (var member in plan.Members)
        {
            artist.Members.Add(new ArtistMember
            {
                Id = Guid.NewGuid(),
                SortOrder = order++,
                Name = member.Name,
                Role = member.Role,
                Biography = member.Biography,
                VoiceCreationPrompt = member.VoiceCreationPrompt,
            });
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

        return artist;
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
