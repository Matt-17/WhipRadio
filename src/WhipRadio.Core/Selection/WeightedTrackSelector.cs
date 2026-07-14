using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Selection;

/// <summary>
/// Picks the next track by weighted random over a filtered candidate set.
///
/// Hard rule (never relaxed below the short recent window): a track that already
/// aired in the current show or the previous show is excluded, so the same song is
/// never chosen twice within a format's back-to-back shows. When the library is
/// small and that window would empty the pool, the previous-show portion is
/// dropped first, keeping only the last <see cref="SelectionSettings.RecentExclusionCount"/>
/// plays excluded — graceful relaxation that preserves the no-immediate-repeat
/// guarantee even when <c>MaxLibrarySize</c> is tight.
///
/// Soft, format-aware rules (each applied only when it leaves candidates, mirroring
/// the existing vocal-preference pattern): genre/subgenre chain, subgenre rotation
/// (avoid the same subgenre back-to-back), artist-repeat cap (no back-to-back
/// artist, at most N per lookback window), host-genre weight boost, and mode-specific
/// behavior for artist-feature / spotlight / theme / freeform formats.
///
/// Track weight is scaled by the artist's overall vote standing, so badly rated
/// artists slowly rotate out. <see cref="SelectionSettings.DiversityEnabled"/> is
/// the master switch: when off, the selector falls back to the legacy last-N
/// exclusion behavior.
/// </summary>
public class WeightedTrackSelector(ITrackRepository repository, Random? random = null) : ITrackSelector
{
    /// <summary>Legacy short recent-exclusion window used when no show windows are supplied.</summary>
    public const int RecentExclusionCount = 3;

    /// <summary>Upper bound on how many play-log rows the show-window query reads.</summary>
    private const int MaxExclusionCount = 200;

    private readonly Random _random = random ?? Random.Shared;

    public async Task<Track?> PickNextAsync(ShowContext context, CancellationToken ct)
    {
        var candidates = await repository.GetCandidatesAsync(ct);
        var selection = context.Selection ?? SelectionSettings.Default;

        if (!selection.DiversityEnabled || context.ShowWindows is null)
        {
            var legacyRecent = await repository.GetRecentlyPlayedTrackIdsAsync(
                context.ShowWindows is null ? RecentExclusionCount : selection.RecentExclusionCount, ct);
            return Pick(candidates, context, legacyRecent, _random);
        }

        var windows = context.ShowWindows;
        var rules = context.Format?.SelectionRules ?? FormatSelectionRules.Default;
        var hardExcluded = await repository.GetTrackIdsPlayedSinceAsync(windows.ExclusionSinceUtc, MaxExclusionCount, ct);
        var recentExcluded = await repository.GetRecentlyPlayedTrackIdsAsync(selection.RecentExclusionCount, ct);
        // Fetch enough refs for the widest lookback in play (per-format cap or station default).
        var lookback = Math.Max(rules.ArtistLookbackTracks, selection.ArtistLookbackTracks);
        var recentRefs = await repository.GetRecentPlayedRefsAsync(
            Math.Max(lookback, selection.RecentExclusionCount), ct);

        return Pick(candidates, context, hardExcluded, recentExcluded, recentRefs, rules, selection, _random);
    }

    /// <summary>Legacy pure entry point: last-N exclusion only (existing tests).</summary>
    public static Track? Pick(
        IReadOnlyList<Track> candidates,
        ShowContext context,
        IReadOnlyList<Guid> recentlyPlayedIds,
        Random random)
        => Pick(candidates, context, recentlyPlayedIds, recentlyPlayedIds, [], FormatSelectionRules.Default, SelectionSettings.Default, random);

    /// <summary>
    /// Pure selection over an in-memory candidate list (unit-testable). The hard
    /// exclusion is layered: first the full current+previous show window, then —
    /// if that empties every tier — only the short recent window. The short recent
    /// window is the absolute floor and is never relaxed away.
    /// </summary>
    public static Track? Pick(
        IReadOnlyList<Track> candidates,
        ShowContext context,
        IReadOnlyList<Guid> hardExcludedIds,
        IReadOnlyList<Guid> recentExcludedIds,
        IReadOnlyList<PlayedTrackRef> recentRefs,
        FormatSelectionRules rules,
        SelectionSettings selection,
        Random random)
    {
        var hardSet = hardExcludedIds.ToHashSet();
        var recentSet = recentExcludedIds.ToHashSet();

        // Exclusion tiers, strictest first: full show window, then short-recent only.
        // recentSet is always enforced; hardSet is the relaxable previous/current-show layer.
        // HashSets keep the per-candidate exclusion check O(1) across the (up to 6) attempts.
        IReadOnlySet<Guid>[] exclusionTiers =
        [
            new HashSet<Guid>(hardSet.Concat(recentSet)),
            recentSet,
        ];

        // Artist vote standing is the same for every attempt — compute it once.
        var artistFactors = ComputeArtistFactors(candidates);

        // Keep the no-repeat rule as long as possible: exclusion is the outer loop
        // (strictest first), mode relaxation is the inner loop. So a feature format
        // first relaxes its mode (SingleArtistFeature -> SpotlightArtist -> Standard)
        // before the previous-show no-repeat window is ever dropped.
        foreach (var excluded in exclusionTiers)
        {
            foreach (var mode in GetModeRelaxationTiers(rules.Mode))
            {
                var relaxedRules = rules with { Mode = mode };
                var result = TryPickWithExclusion(candidates, context, excluded, recentRefs, relaxedRules, selection, artistFactors, random);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<SelectionMode> GetModeRelaxationTiers(SelectionMode mode) => mode switch
    {
        SelectionMode.SingleArtistFeature => [SelectionMode.SingleArtistFeature, SelectionMode.SpotlightArtist, SelectionMode.StandardRotation],
        SelectionMode.SpotlightArtist => [SelectionMode.SpotlightArtist, SelectionMode.StandardRotation],
        SelectionMode.ThemeBlock => [SelectionMode.ThemeBlock, SelectionMode.StandardRotation],
        SelectionMode.Freeform => [SelectionMode.Freeform],
        _ => [SelectionMode.StandardRotation],
    };

    private static Track? TryPickWithExclusion(
        IReadOnlyList<Track> candidates,
        ShowContext context,
        IReadOnlySet<Guid> excluded,
        IReadOnlyList<PlayedTrackRef> recentRefs,
        FormatSelectionRules rules,
        SelectionSettings selection,
        IReadOnlyDictionary<Guid, double> artistFactors,
        Random random)
    {
        var pool = candidates
            .Where(t => !t.IsRetired)
            .Where(t => !excluded.Contains(t.Id))
            .ToList();

        if (pool.Count == 0)
        {
            return null;
        }

        // Mode hard-filter (SingleArtistFeature narrows to the featured artist).
        pool = ApplyModeFilter(pool, rules);
        if (pool.Count == 0)
        {
            return null;
        }

        // Timing cap (soft): prefer tracks that fit before the next scheduled
        // package. Never empties the pool — the caller re-checks the pick.
        pool = ApplyDurationCap(pool, selection);

        // Genre filter with fallback chain: subgenre match -> genre match -> anything.
        // Skipped entirely for Freeform formats. Subgenre rotation is integrated
        // here so a format whose subgenre just played prefers a sibling subgenre
        // instead of narrowing back to the same one.
        pool = ApplyGenreChain(pool, context, rules, recentRefs, selection);
        if (pool.Count == 0)
        {
            return null;
        }

        // Soft filters: each only applied when it leaves candidates.
        // Subgenre rotation for the no-context-subgenre case (broad genre formats)
        // is handled here; the context-subgenre case was handled in the chain above.
        pool = ApplySubgenreRotation(pool, recentRefs, rules, selection);
        pool = ApplyArtistCap(pool, recentRefs, rules, selection);
        pool = ApplyVocalPreference(pool, context);

        if (pool.Count == 0)
        {
            return null;
        }

        return WeightedRandomPick(pool, artistFactors, rules, selection, context, random);
    }

    private static List<Track> ApplyDurationCap(List<Track> pool, SelectionSettings selection)
    {
        if (selection.MaxTrackDurationSeconds is not double cap || pool.Count == 0)
        {
            return pool;
        }

        var fitting = pool.Where(t => t.DurationSeconds <= cap).ToList();
        return fitting.Count > 0 ? fitting : pool;
    }

    private static List<Track> ApplyModeFilter(List<Track> pool, FormatSelectionRules rules)
    {
        if (rules.Mode == SelectionMode.SingleArtistFeature && rules.FeaturedArtistId is Guid featured)
        {
            var narrowed = pool.Where(t => t.ArtistId == featured).ToList();
            // If the feature artist has no unplayed tracks, signal exhaustion by returning empty;
            // the mode-relaxation loop will then try SpotlightArtist / StandardRotation.
            return narrowed;
        }

        return pool;
    }

    private static List<Track> ApplyGenreChain(
        List<Track> pool, ShowContext context, FormatSelectionRules rules,
        IReadOnlyList<PlayedTrackRef> recentRefs, SelectionSettings selection)
    {
        if (rules.Mode == SelectionMode.Freeform)
        {
            return pool;
        }

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
                    // Subgenre rotation: if the format's subgenre just played, prefer
                    // sibling subgenres within the same genre (soft — fallback to the
                    // format subgenre when no siblings exist).
                    if (selection.SubgenreRotation && rules.SubgenreRotation && recentRefs.Count > 0
                        && string.Equals(recentRefs[0].Subgenre, context.Subgenre, StringComparison.OrdinalIgnoreCase))
                    {
                        var rotated = pool
                            .Where(t => !string.Equals(t.Subgenre, context.Subgenre, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (rotated.Count > 0)
                        {
                            return rotated;
                        }
                    }

                    pool = subgenreMatched;
                }
            }
        }

        return pool;
    }

    private static List<Track> ApplySubgenreRotation(
        List<Track> pool, IReadOnlyList<PlayedTrackRef> recentRefs,
        FormatSelectionRules rules, SelectionSettings selection)
    {
        if (!(selection.SubgenreRotation && rules.SubgenreRotation) || pool.Count == 0 || recentRefs.Count == 0)
        {
            return pool;
        }

        var lastSubgenre = recentRefs[0].Subgenre;
        if (string.IsNullOrWhiteSpace(lastSubgenre))
        {
            return pool;
        }

        var rotated = pool
            .Where(t => !string.Equals(t.Subgenre, lastSubgenre, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return rotated.Count > 0 ? rotated : pool;
    }

    private static List<Track> ApplyArtistCap(
        List<Track> pool,
        IReadOnlyList<PlayedTrackRef> recentRefs,
        FormatSelectionRules rules,
        SelectionSettings selection)
    {
        // Artist-feature and spotlight formats deliberately concentrate on one artist.
        if (rules.Mode is SelectionMode.SingleArtistFeature or SelectionMode.SpotlightArtist
            || pool.Count == 0 || recentRefs.Count == 0)
        {
            return pool;
        }

        var lookback = recentRefs.Take(Math.Max(1, rules.ArtistLookbackTracks)).ToList();
        var maxPerHour = rules.MaxArtistPlaysPerHour ?? selection.MaxArtistPlaysPerHour;
        if (maxPerHour <= 0)
        {
            return pool;
        }

        // No same artist back-to-back (soft: only if alternatives exist).
        // Imported real music has no Artist entity — its display artist name is
        // the cap key so the same real artist also doesn't repeat back-to-back.
        if (ArtistKey(lookback[0].ArtistId, lookback[0].ImportedArtist) is { } lastArtist)
        {
            var withoutLast = pool
                .Where(t => !string.Equals(ArtistKey(t.ArtistId, t.ImportedArtist), lastArtist, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (withoutLast.Count > 0)
            {
                pool = withoutLast;
            }
        }

        // At most maxPerHour plays per artist within the lookback window (soft).
        var artistCounts = lookback
            .Select(r => ArtistKey(r.ArtistId, r.ImportedArtist))
            .Where(key => key is not null)
            .GroupBy(key => key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var capped = pool
            .Where(t => ArtistKey(t.ArtistId, t.ImportedArtist) is not { } key
                || !artistCounts.TryGetValue(key, out var count)
                || count < maxPerHour)
            .ToList();
        return capped.Count > 0 ? capped : pool;
    }

    private static string? ArtistKey(Guid? artistId, string? importedArtist)
        => artistId?.ToString()
            ?? (string.IsNullOrWhiteSpace(importedArtist) ? null : importedArtist.Trim());

    private static List<Track> ApplyVocalPreference(List<Track> pool, ShowContext context)
    {
        if (context.Moderator.PrefersVocals is bool prefersVocals)
        {
            var vocalMatched = pool.Where(t => t.HasVocals == prefersVocals).ToList();
            if (vocalMatched.Count > 0)
            {
                return vocalMatched;
            }
        }

        return pool;
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
        FormatSelectionRules rules,
        SelectionSettings selection,
        ShowContext context,
        Random random)
    {
        var hostGenres = selection.PreferHostGenres && rules.PreferHostGenres
            ? ParseHostGenres(context.Moderator.PreferredGenres)
            : [];

        var weights = pool
            .Select(t =>
            {
                var weight = TrackWeighting.Weight(t, selection.FatigueFactor);
                if (t.ArtistId is Guid artistId && artistFactors.TryGetValue(artistId, out var factor))
                {
                    weight *= factor;
                }

                if (rules.Mode == SelectionMode.SpotlightArtist
                    && rules.FeaturedArtistId is Guid featured
                    && t.ArtistId == featured)
                {
                    weight *= 3.0;
                }

                if (rules.Mode == SelectionMode.ThemeBlock
                    && !string.IsNullOrWhiteSpace(rules.Theme)
                    && t.Style.Contains(rules.Theme, StringComparison.OrdinalIgnoreCase))
                {
                    weight *= 2.0;
                }

                if (hostGenres.Count > 0
                    && hostGenres.Contains(t.Genre, StringComparer.OrdinalIgnoreCase))
                {
                    weight *= 1.3;
                }

                return weight;
            })
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

    private static HashSet<string> ParseHostGenres(string? csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
