using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Selection;

/// <summary>
/// Picks the next track by weighted random over the filtered candidate set:
/// matching genre (subgenre preferred, fallback to any genre), respecting the
/// moderator's vocal preference when satisfiable, excluding retired tracks and
/// the last 3 played. Track weight is additionally scaled by the artist's
/// overall vote standing, so badly rated artists slowly rotate out.
/// </summary>
public class WeightedTrackSelector(ITrackRepository repository, Random? random = null) : ITrackSelector
{
    public const int RecentExclusionCount = 3;

    private readonly Random _random = random ?? Random.Shared;

    public async Task<Track?> PickNextAsync(ShowContext context, CancellationToken ct)
    {
        var candidates = await repository.GetCandidatesAsync(ct);
        var recent = await repository.GetRecentlyPlayedTrackIdsAsync(RecentExclusionCount, ct);
        return Pick(candidates, context, recent, _random);
    }

    /// <summary>Pure selection over an in-memory candidate list (unit-testable).</summary>
    public static Track? Pick(
        IReadOnlyList<Track> candidates,
        ShowContext context,
        IReadOnlyList<Guid> recentlyPlayedIds,
        Random random)
    {
        var pool = candidates
            .Where(t => !t.IsRetired)
            .Where(t => !recentlyPlayedIds.Contains(t.Id))
            .ToList();

        if (pool.Count == 0)
        {
            return null;
        }

        // Genre filter with fallback chain: subgenre match → genre match → anything.
        var genreMatched = pool
            .Where(t => string.Equals(t.Genre, context.Genre, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (genreMatched.Count > 0)
        {
            pool = genreMatched;
            if (!string.IsNullOrEmpty(context.Subgenre))
            {
                var subgenreMatched = pool
                    .Where(t => string.Equals(t.Subgenre, context.Subgenre, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (subgenreMatched.Count > 0)
                {
                    pool = subgenreMatched;
                }
            }
        }

        // Vocal preference is a soft filter: only applied when it leaves candidates.
        if (context.Moderator.PrefersVocals is bool prefersVocals)
        {
            var vocalMatched = pool.Where(t => t.HasVocals == prefersVocals).ToList();
            if (vocalMatched.Count > 0)
            {
                pool = vocalMatched;
            }
        }

        var artistFactors = ComputeArtistFactors(candidates);
        return WeightedRandomPick(pool, artistFactors, random);
    }

    /// <summary>
    /// Per-artist multiplier from the artist's net votes across ALL their tracks:
    /// clamp(0.25 … 2.0, 1 + 0.05 * netVotes). Artists with disliked catalogs
    /// fade out; loved ones get more rotation.
    /// </summary>
    public static Dictionary<Guid, double> ComputeArtistFactors(IReadOnlyList<Track> allTracks)
    {
        return allTracks
            .Where(t => t.ArtistId is not null)
            .GroupBy(t => t.ArtistId!.Value)
            .ToDictionary(
                g => g.Key,
                g => Math.Clamp(1 + 0.05 * g.Sum(t => t.UpVotes - t.DownVotes), 0.25, 2.0));
    }

    private static Track WeightedRandomPick(
        IReadOnlyList<Track> pool,
        IReadOnlyDictionary<Guid, double> artistFactors,
        Random random)
    {
        var weights = pool
            .Select(t => TrackWeighting.Weight(t) *
                (t.ArtistId is Guid artistId && artistFactors.TryGetValue(artistId, out var factor) ? factor : 1.0))
            .ToArray();
        var total = weights.Sum();
        var roll = random.NextDouble() * total;

        for (var i = 0; i < pool.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
            {
                return pool[i];
            }
        }

        return pool[^1];
    }
}
