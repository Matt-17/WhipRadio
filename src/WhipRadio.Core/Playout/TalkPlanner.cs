using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Playout;

public sealed record HostTalkProfile(
    int BreakFrequencyTracks,
    int MinPartsPerBreak,
    int MaxPartsPerBreak,
    IReadOnlySet<AnnouncementKind> AllowedKinds,
    int ExactReplayTolerance,
    double EvergreenBitTolerance)
{
    private static readonly HashSet<AnnouncementKind> DefaultAllowedKinds =
    [
        AnnouncementKind.SongIntro,
        AnnouncementKind.SongOutro,
        AnnouncementKind.Banter,
        AnnouncementKind.PersonalNote,
        AnnouncementKind.Joke,
        AnnouncementKind.TalkBit,
        AnnouncementKind.Jingle,
        AnnouncementKind.ListenerGreeting,
        AnnouncementKind.RequestDedication,
        AnnouncementKind.StationId,
        AnnouncementKind.Weather,
        AnnouncementKind.HostChange,
    ];

    public bool Allows(AnnouncementKind kind)
        => AllowedKinds.Count == 0 || AllowedKinds.Contains(kind);

    public static HostTalkProfile FromModerator(Moderator moderator)
        => new(
            Math.Max(0, moderator.TalkBreakFrequencyTracks),
            Math.Clamp(moderator.MinTalkPartsPerBreak, 0, 10),
            Math.Clamp(Math.Max(moderator.MaxTalkPartsPerBreak, moderator.MinTalkPartsPerBreak), 1, 10),
            ParseAllowedKinds(moderator.AllowedTalkPartKinds),
            Math.Max(0, moderator.ExactReplayTolerance),
            Math.Clamp(moderator.EvergreenBitTolerance, 0, 1));

    private static IReadOnlySet<AnnouncementKind> ParseAllowedKinds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAllowedKinds;
        }

        var result = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Enum.TryParse<AnnouncementKind>(part, ignoreCase: true, out var parsed) ? parsed : (AnnouncementKind?)null)
            .Where(kind => kind is not null)
            .Select(kind => kind!.Value)
            .ToHashSet();

        return result.Count == 0 ? DefaultAllowedKinds : result;
    }
}

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

    public static double EffectiveTalkativeness(Moderator moderator, Format? format)
        => EffectiveTalkativeness(moderator.Talkativeness, format?.TalkDensity ?? format?.Talkativeness);

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

    public static int PickGapTalkCount(
        Random random,
        bool hasMandatoryTalk,
        Moderator moderator,
        Format? format)
    {
        var profile = HostTalkProfile.FromModerator(moderator);
        if (profile.BreakFrequencyTracks <= 0)
        {
            return 0;
        }

        if (!hasMandatoryTalk
            && profile.BreakFrequencyTracks > 1
            && random.Next(profile.BreakFrequencyTracks) != 0)
        {
            return 0;
        }

        var count = PickGapTalkCount(random, hasMandatoryTalk, EffectiveTalkativeness(moderator, format));
        if (count == 0)
        {
            return 0;
        }

        return Math.Clamp(count, profile.MinPartsPerBreak, profile.MaxPartsPerBreak);
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

    public static string PickLengthHint(Random random, TalkDepth talkDepth, double talkativeness = 0.5)
    {
        var roll = random.NextDouble();
        return talkDepth switch
        {
            TalkDepth.NameOnly => "ONE short sentence - only identify the song, artist, or what is next. No story.",
            TalkDepth.Light => roll < 0.65
                ? "ONE short sentence - just a quick link between songs."
                : "2-3 sentences.",
            TalkDepth.Detailed => roll switch
            {
                < 0.20 => "ONE short sentence - just a quick link between songs.",
                < 0.75 => "2-3 sentences with a little context or mood.",
                _ => "4-6 sentences with useful context, but no invented facts.",
            },
            TalkDepth.DeepDive => roll > 0.65 - 0.15 * Math.Clamp(talkativeness, 0, 1)
                ? "a longer piece of 8-12 sentences - take your time and tell a little story from the available context."
                : "4-6 sentences with useful background, mood, and a smooth setup.",
            _ => PickLengthHint(random, talkativeness),
        };
    }

    public static string ScriptInstruction(TalkDepth talkDepth, AnnouncementKind kind)
    {
        var subject = kind is AnnouncementKind.SongIntro ? "intro" : "talk";
        return talkDepth switch
        {
            TalkDepth.NameOnly => $"Talk depth is NameOnly: make this {subject} a single plain line. Mention only the title, artist, or immediate transition.",
            TalkDepth.Light => $"Talk depth is Light: keep this {subject} quick and organic, usually one thought.",
            TalkDepth.Detailed => $"Talk depth is Detailed: add real context from the prompt, a short observation, or a useful bridge.",
            TalkDepth.DeepDive => $"Talk depth is DeepDive: use the available context for a richer setup, story, or analysis without inventing facts.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// How many waiting listener messages the host reads in one mailbag segment (1–10).
    /// A reserved host takes them one at a time; a chatty one may clear the whole pile.
    /// </summary>
    public static int PickGreetingBatchSize(Random random, double talkativeness)
    {
        talkativeness = Math.Clamp(talkativeness, 0, 1);
        var maxBatch = 1 + (int)Math.Round(9 * talkativeness);
        return 1 + random.Next(maxBatch);
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

    public static AnnouncementKind PickFreeTalkKind(
        Random random,
        bool hasNextTrack,
        bool hasPreviousTrack,
        HostTalkProfile profile)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = PickFreeTalkKind(random, hasNextTrack, hasPreviousTrack);
            if (profile.Allows(candidate))
            {
                return candidate;
            }
        }

        foreach (var fallback in new[]
                 {
                     AnnouncementKind.SongIntro,
                     AnnouncementKind.SongOutro,
                     AnnouncementKind.Banter,
                     AnnouncementKind.PersonalNote,
                     AnnouncementKind.Joke,
                     AnnouncementKind.TalkBit,
                     AnnouncementKind.StationId,
                 })
        {
            if ((fallback != AnnouncementKind.SongIntro || hasNextTrack)
                && (fallback != AnnouncementKind.SongOutro || hasPreviousTrack)
                && profile.Allows(fallback))
            {
                return fallback;
            }
        }

        return AnnouncementKind.StationId;
    }
}
