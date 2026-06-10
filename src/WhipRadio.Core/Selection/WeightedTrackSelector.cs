using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Selection;

/// <summary>
/// Picks the next track by weighted random over the filtered candidate set:
/// matching slot genre (fallback: all genres), respecting the moderator's
/// vocal preference when satisfiable, excluding retired and the last 3 played.
/// </summary>
public class WeightedTrackSelector(ITrackRepository repository, Random? random = null) : ITrackSelector
{
    public const int RecentExclusionCount = 3;

    private readonly Random _random = random ?? Random.Shared;

    public async Task<Track?> PickNextAsync(ScheduleSlot slot, Moderator moderator, CancellationToken ct)
    {
        var candidates = await repository.GetCandidatesAsync(ct);
        var recent = await repository.GetRecentlyPlayedTrackIdsAsync(RecentExclusionCount, ct);
        return Pick(candidates, slot, moderator, recent, _random);
    }

    /// <summary>Pure selection over an in-memory candidate list (unit-testable).</summary>
    public static Track? Pick(
        IReadOnlyList<Track> candidates,
        ScheduleSlot slot,
        Moderator moderator,
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

        // Genre filter with fallback to any genre when nothing matches the slot.
        var genreMatched = pool
            .Where(t => string.Equals(t.Genre, slot.Genre, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (genreMatched.Count > 0)
        {
            pool = genreMatched;
        }

        // Vocal preference is a soft filter: only applied when it leaves candidates.
        if (moderator.PrefersVocals is bool prefersVocals)
        {
            var vocalMatched = pool.Where(t => t.HasVocals == prefersVocals).ToList();
            if (vocalMatched.Count > 0)
            {
                pool = vocalMatched;
            }
        }

        return WeightedRandomPick(pool, random);
    }

    private static Track WeightedRandomPick(IReadOnlyList<Track> pool, Random random)
    {
        var weights = pool.Select(TrackWeighting.Weight).ToArray();
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
