using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Selection;

/// <summary>Pure weighting math for track rotation (see Plan.md §6).</summary>
public static class TrackWeighting
{
    public const double MinimumVoteWeight = 0.1;

    /// <summary>Default play-count fatigue coefficient (1 / (1 + PlayCount * factor)).</summary>
    public const double DefaultFatigueFactor = 0.15;

    /// <summary>
    /// weight = max(0.1, 1 + 0.5*UpVotes - 0.7*DownVotes) * (1 / (1 + PlayCount * fatigueFactor))
    /// </summary>
    public static double Weight(int upVotes, int downVotes, int playCount)
        => Weight(upVotes, downVotes, playCount, DefaultFatigueFactor);

    /// <summary>
    /// weight = max(0.1, 1 + 0.5*UpVotes - 0.7*DownVotes) * (1 / (1 + PlayCount * fatigueFactor))
    /// </summary>
    public static double Weight(int upVotes, int downVotes, int playCount, double fatigueFactor)
    {
        var voteWeight = Math.Max(MinimumVoteWeight, 1 + 0.5 * upVotes - 0.7 * downVotes);
        var fatigue = 1.0 / (1.0 + playCount * fatigueFactor);
        return voteWeight * fatigue;
    }

    public static double Weight(Track track) => Weight(track.UpVotes, track.DownVotes, track.PlayCount);

    public static double Weight(Track track, double fatigueFactor)
        => Weight(track.UpVotes, track.DownVotes, track.PlayCount, fatigueFactor);

    /// <summary>Retire rule: DownVotes >= 5 &amp;&amp; DownVotes > 2 * UpVotes.</summary>
    public static bool ShouldRetire(int upVotes, int downVotes)
        => downVotes >= 5 && downVotes > 2 * upVotes;

    public static bool ShouldRetire(Track track) => ShouldRetire(track.UpVotes, track.DownVotes);
}
