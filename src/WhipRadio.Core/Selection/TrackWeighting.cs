using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Selection;

/// <summary>Pure weighting math for track rotation (see Plan.md §6).</summary>
public static class TrackWeighting
{
    public const double MinimumVoteWeight = 0.1;

    /// <summary>
    /// weight = max(0.1, 1 + 0.5*UpVotes - 0.7*DownVotes) * (1 / (1 + PlayCount * 0.15))
    /// </summary>
    public static double Weight(int upVotes, int downVotes, int playCount)
    {
        var voteWeight = Math.Max(MinimumVoteWeight, 1 + 0.5 * upVotes - 0.7 * downVotes);
        var fatigueFactor = 1.0 / (1.0 + playCount * 0.15);
        return voteWeight * fatigueFactor;
    }

    public static double Weight(Track track) => Weight(track.UpVotes, track.DownVotes, track.PlayCount);

    /// <summary>Retire rule: DownVotes >= 5 &amp;&amp; DownVotes > 2 * UpVotes.</summary>
    public static bool ShouldRetire(int upVotes, int downVotes)
        => downVotes >= 5 && downVotes > 2 * upVotes;

    public static bool ShouldRetire(Track track) => ShouldRetire(track.UpVotes, track.DownVotes);
}
