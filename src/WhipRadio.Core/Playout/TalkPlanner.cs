using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Playout;

/// <summary>
/// The "moderator's mood" for the gap between two songs: talks are produced
/// fresh for the gap they air in. A gap may have NO talk at all, one, or several
/// chained ones ("that was …" → greeting → coffee story → "up next …").
/// How chatty a gap gets depends on the host's and the format's talkativeness.
/// </summary>
public static class TalkPlanner
{
    /// <summary>Effective talkativeness: the format tempers the host when one is on air.</summary>
    public static double EffectiveTalkativeness(double moderator, double? format)
        => Math.Clamp(format is null ? moderator : (moderator + format.Value) / 2, 0, 1);

    /// <summary>
    /// How many free talks the host feels like doing in this gap (0–3).
    /// talkativeness 0 = almost always straight into the music,
    /// 0.5 = mostly one talk, 1 = rarely silent, often chains two or three.
    /// Mandatory items (weather/greeting) already fill the gap, so free talks thin out.
    /// </summary>
    public static int PickGapTalkCount(Random random, bool hasMandatoryTalk, double talkativeness)
    {
        talkativeness = Math.Clamp(talkativeness, 0, 1);

        var pNone = Math.Clamp(0.65 - 0.6 * talkativeness, 0.05, 0.9);
        var pTwo = 0.30 * talkativeness;
        var pThree = 0.12 * talkativeness;

        if (hasMandatoryTalk)
        {
            pNone = Math.Min(0.9, pNone + 0.3);
            pTwo /= 2;
            pThree = 0;
        }

        var roll = random.NextDouble();
        if (roll < pNone)
        {
            return 0;
        }

        if (roll > 1 - pThree)
        {
            return 3;
        }

        return roll > 1 - pThree - pTwo ? 2 : 1;
    }

    /// <summary>Length instruction handed to the script writer (10 s … a few minutes).
    /// Talkative hosts ramble into longer pieces more often.</summary>
    public static string PickLengthHint(Random random, double talkativeness = 0.5)
    {
        var storyChance = 0.05 + 0.15 * Math.Clamp(talkativeness, 0, 1);
        var roll = random.NextDouble();

        if (roll > 1 - storyChance)
        {
            return "a longer piece of 8-12 sentences — take your time and tell a little story.";
        }

        return roll switch
        {
            < 0.35 => "ONE short sentence — just a quick link between songs.",
            < 0.70 => "2-3 sentences.",
            _ => "4-6 sentences.",
        };
    }

    /// <summary>What the host talks about in a free slot.</summary>
    public static AnnouncementKind PickFreeTalkKind(Random random, bool hasNextTrack, bool hasPreviousTrack)
    {
        var roll = random.NextDouble();

        if (hasPreviousTrack && roll < 0.20)
        {
            return AnnouncementKind.SongOutro; // "that was …"
        }

        if (hasNextTrack && roll < 0.55)
        {
            return AnnouncementKind.SongIntro;
        }

        return roll switch
        {
            < 0.70 => AnnouncementKind.Banter,
            < 0.90 => AnnouncementKind.PersonalNote,
            _ => AnnouncementKind.Joke,
        };
    }
}
