using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed record TrackSearchResult(
    Guid Id,
    string Title,
    string ArtistName,
    string Genre,
    string? Subgenre,
    double DurationSeconds,
    int UpVotes,
    int DownVotes);

public sealed class TrackQueryService(IDbContextFactory<RadioDbContext> dbFactory)
{
    public async Task<IReadOnlyList<TrackSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct)
    {
        string[] tokens = query.Split([' ', ',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int take = Math.Clamp(limit <= 0 ? 5 : limit, 1, 10);
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Track> tracks = await db.Tracks.AsNoTracking()
            .Include(track => track.Artist)
            .Where(track => !track.IsRetired)
            .OrderByDescending(track => track.CreatedAt)
            .Take(800)
            .ToListAsync(ct);

        IEnumerable<Track> matches = tokens.Length == 0
            ? tracks
            : tracks.Where(track => tokens.Any(token => Matches(track, token)));

        return matches
            .OrderByDescending(track => Score(track, tokens))
            .ThenByDescending(track => track.UpVotes - track.DownVotes)
            .ThenByDescending(track => track.CreatedAt)
            .Take(take)
            .Select(track => new TrackSearchResult(
                track.Id,
                track.Title,
                track.Artist?.Name ?? "Unknown Artist",
                track.Genre,
                track.Subgenre,
                track.DurationSeconds,
                track.UpVotes,
                track.DownVotes))
            .ToList();
    }

    private static bool Matches(Track track, string token)
        => Contains(track.Title, token)
            || Contains(track.Artist?.Name, token)
            || Contains(track.Genre, token)
            || Contains(track.Subgenre, token)
            || Contains(track.Style, token)
            || Contains(track.SongStory, token);

    private static int Score(Track track, IReadOnlyList<string> tokens)
        => tokens.Sum(token =>
            EqualsIgnoreCase(track.Genre, token) || EqualsIgnoreCase(track.Subgenre, token) ? 8 :
            Contains(track.Artist?.Name, token) ? 5 :
            Contains(track.Title, token) ? 4 :
            Contains(track.Style, token) || Contains(track.SongStory, token) ? 2 :
            0);

    private static bool Contains(string? value, string token)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string? value, string token)
        => string.Equals(value, token, StringComparison.OrdinalIgnoreCase);
}
