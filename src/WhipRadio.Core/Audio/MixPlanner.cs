using System.Globalization;
using System.Text.Json;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Audio;

public enum MixStrategy
{
    /// <summary>First-class default for talk; a legitimate random choice everywhere.</summary>
    HardCut,

    /// <summary>Equal-power crossfade, anchor = end − duration.</summary>
    EnergyFade,

    /// <summary>Equal-power crossfade anchored at the outgoing OutroStart.</summary>
    OutroBridgeIn,

    /// <summary>Crossfade with beat-grid alignment (no tempo stretching).</summary>
    BeatAlignedFade,

    /// <summary>Talk over the incoming song intro ("hit the post").</summary>
    IntroTalkOver,

    /// <summary>Talk starts over the outgoing outro (ducked); song ends under talk.</summary>
    OutroTalkOver,
}

public enum PairKind
{
    TalkToTalk,
    TalkToSong,
    SongToTalk,
    SongToSong,
}

/// <summary>Everything the planner knows about one item of a pair.
/// HostTalkativeness (talk items only, 0–1) pulls talk-over strategies up or
/// down — a chatty host rides the intro far more often than a reserved one.</summary>
public sealed record ItemInfo(
    PlayoutItemType ItemType,
    MediaAnalysis? Analysis,
    double DurationSeconds,
    double? HostTalkativeness = null);

/// <summary>The ephemeral per-pair decision (logged, never stored as state).</summary>
public sealed record TransitionPlan(
    MixStrategy Strategy,
    double OverlapSeconds,
    int GapMs,
    double? IncomingStartOffsetSeconds,
    double DuckLevelDb,
    string ReasonTrace);

/// <summary>Hot-reloadable mixer parameters (snapshot of StationSettings).</summary>
public sealed record MixerSettings(
    double TargetLufs = -16.0,
    double MaxMakeupGainDb = 6.0,
    double DuckLevelDb = -12.0,
    int DuckRampMs = 800,
    double DefaultCrossfadeSeconds = 5.0,
    double BeatAlignBpmTolerancePct = 5.0,
    int HardCutGapAfterTalkMsMin = 200,
    int HardCutGapAfterTalkMsMax = 600,
    int HardCutGapSongMsMin = 0,
    int HardCutGapSongMsMax = 150,
    int PostHitSafetyMs = 800,
    string StrategyWeightsJson = "");

public interface IMixPlanner
{
    TransitionPlan Plan(ItemInfo outgoing, ItemInfo incoming, MixerSettings settings);
}

/// <summary>
/// Pure strategy decision: hard eligibility preconditions shrink the candidate
/// set (worst case {HardCut} — a perfectly good radio transition, never an
/// error), then a weighted random pick keeps real-radio variety.
/// </summary>
public sealed class MixPlanner(IRandomSource random) : IMixPlanner
{
    private static readonly IReadOnlyDictionary<PairKind, IReadOnlyDictionary<MixStrategy, int>> DefaultWeights =
        new Dictionary<PairKind, IReadOnlyDictionary<MixStrategy, int>>
        {
            [PairKind.TalkToTalk] = new Dictionary<MixStrategy, int> { [MixStrategy.HardCut] = 100 },
            [PairKind.TalkToSong] = new Dictionary<MixStrategy, int>
            {
                [MixStrategy.HardCut] = 40,
                [MixStrategy.IntroTalkOver] = 60,
            },
            [PairKind.SongToTalk] = new Dictionary<MixStrategy, int>
            {
                [MixStrategy.HardCut] = 55,
                [MixStrategy.OutroTalkOver] = 45,
            },
            [PairKind.SongToSong] = new Dictionary<MixStrategy, int>
            {
                [MixStrategy.HardCut] = 20,
                [MixStrategy.EnergyFade] = 25,
                [MixStrategy.OutroBridgeIn] = 25,
                [MixStrategy.BeatAlignedFade] = 30,
            },
        };

    public TransitionPlan Plan(ItemInfo outgoing, ItemInfo incoming, MixerSettings settings)
    {
        var pairKind = GetPairKind(outgoing, incoming);
        var eligible = BuildEligibleSet(pairKind, outgoing, incoming, settings, out var traceNotes);
        var weights = ResolveWeights(settings.StrategyWeightsJson, pairKind);

        // The host has a say: the talk side's talkativeness scales the
        // talk-over weights (0 → ×0.5, 0.5 → ×1, 1 → ×1.5).
        var talkativeness = pairKind switch
        {
            PairKind.TalkToSong => outgoing.HostTalkativeness,
            PairKind.SongToTalk => incoming.HostTalkativeness,
            _ => null,
        };
        if (talkativeness is { } t)
        {
            var factor = 0.5 + Math.Clamp(t, 0, 1);
            weights = weights.ToDictionary(
                kv => kv.Key,
                kv => kv.Key is MixStrategy.IntroTalkOver or MixStrategy.OutroTalkOver
                    ? (int)Math.Round(kv.Value * factor)
                    : kv.Value);
            traceNotes.Add(string.Create(CultureInfo.InvariantCulture, $"talk={t:F2}(x{factor:F2})"));
        }

        var picked = WeightedPick(eligible, weights, out var pickedWeight);

        var trace = $"{pairKind}; eligible=[{string.Join(",", eligible)}]"
            + (traceNotes.Count > 0 ? $"; {string.Join("; ", traceNotes)}" : "")
            + $"; picked={picked}(w={pickedWeight})";

        return FillParameters(picked, pairKind, outgoing, incoming, settings, trace);
    }

    private static PairKind GetPairKind(ItemInfo outgoing, ItemInfo incoming)
        => (outgoing.ItemType, incoming.ItemType) switch
        {
            (PlayoutItemType.Announcement, PlayoutItemType.Announcement) => PairKind.TalkToTalk,
            (PlayoutItemType.Announcement, PlayoutItemType.Track) => PairKind.TalkToSong,
            (PlayoutItemType.Track, PlayoutItemType.Announcement) => PairKind.SongToTalk,
            _ => PairKind.SongToSong,
        };

    private static List<MixStrategy> BuildEligibleSet(
        PairKind pairKind, ItemInfo outgoing, ItemInfo incoming, MixerSettings settings, out List<string> notes)
    {
        notes = [];
        var eligible = new List<MixStrategy> { MixStrategy.HardCut };

        switch (pairKind)
        {
            case PairKind.SongToSong:
                if (outgoing.DurationSeconds > 2 * settings.DefaultCrossfadeSeconds
                    && incoming.DurationSeconds > 2 * settings.DefaultCrossfadeSeconds)
                {
                    eligible.Add(MixStrategy.EnergyFade);
                }

                if (outgoing.Analysis is { OutroConfidence: >= 0.5, OutroStartSeconds: not null })
                {
                    eligible.Add(MixStrategy.OutroBridgeIn);
                }

                if (outgoing.Analysis is { BpmConfidence: >= 0.6, Bpm: not null, BeatGridJson: not null }
                    && incoming.Analysis is { BpmConfidence: >= 0.6, Bpm: not null, BeatGridJson: not null })
                {
                    var bpmOut = outgoing.Analysis.Bpm!.Value;
                    var bpmIn = incoming.Analysis.Bpm!.Value;
                    var deltaPct = Math.Abs(bpmOut - bpmIn) / bpmOut * 100;
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"dBPM={deltaPct:F1}%"));
                    if (deltaPct <= settings.BeatAlignBpmTolerancePct)
                    {
                        eligible.Add(MixStrategy.BeatAlignedFade);
                    }
                }

                break;

            case PairKind.TalkToSong:
                if (incoming.Analysis is { IntroConfidence: >= 0.5, IntroEndSeconds: { } introEnd }
                    && TransitionMath.CanHitThePost(introEnd, outgoing.DurationSeconds))
                {
                    eligible.Add(MixStrategy.IntroTalkOver);
                }

                break;

            case PairKind.SongToTalk:
                if (outgoing.Analysis is { OutroConfidence: >= 0.5, OutroStartSeconds: not null })
                {
                    eligible.Add(MixStrategy.OutroTalkOver);
                }

                break;
        }

        return eligible;
    }

    private MixStrategy WeightedPick(
        IReadOnlyList<MixStrategy> eligible, IReadOnlyDictionary<MixStrategy, int> weights, out int pickedWeight)
    {
        var total = 0;
        Span<int> cumulative = stackalloc int[eligible.Count];
        for (var i = 0; i < eligible.Count; i++)
        {
            total += Math.Max(0, weights.GetValueOrDefault(eligible[i], 1));
            cumulative[i] = total;
        }

        if (total == 0)
        {
            pickedWeight = 0;
            return MixStrategy.HardCut;
        }

        var roll = random.NextInt(0, total);
        for (var i = 0; i < eligible.Count; i++)
        {
            if (roll < cumulative[i])
            {
                pickedWeight = weights.GetValueOrDefault(eligible[i], 1);
                return eligible[i];
            }
        }

        pickedWeight = weights.GetValueOrDefault(eligible[^1], 1);
        return eligible[^1];
    }

    private TransitionPlan FillParameters(
        MixStrategy strategy, PairKind pairKind, ItemInfo outgoing, ItemInfo incoming,
        MixerSettings settings, string trace)
    {
        switch (strategy)
        {
            case MixStrategy.HardCut:
                var (gapMin, gapMax) = outgoing.ItemType == PlayoutItemType.Announcement
                    ? (settings.HardCutGapAfterTalkMsMin, settings.HardCutGapAfterTalkMsMax)
                    : (settings.HardCutGapSongMsMin, settings.HardCutGapSongMsMax);
                return new TransitionPlan(strategy, 0, TransitionMath.SampleGapMs(random, gapMin, gapMax),
                    null, settings.DuckLevelDb, trace);

            case MixStrategy.EnergyFade:
            case MixStrategy.BeatAlignedFade:
                return new TransitionPlan(strategy, settings.DefaultCrossfadeSeconds, 0,
                    null, settings.DuckLevelDb, trace);

            case MixStrategy.OutroBridgeIn:
                var outroStart = outgoing.Analysis!.OutroStartSeconds!.Value;
                var overlap = Math.Min(
                    Math.Max(settings.DefaultCrossfadeSeconds, outgoing.DurationSeconds - outroStart),
                    outgoing.DurationSeconds / 2);
                return new TransitionPlan(strategy, overlap, 0, null, settings.DuckLevelDb, trace);

            case MixStrategy.IntroTalkOver:
                var introEnd = incoming.Analysis!.IntroEndSeconds!.Value;
                var talkStart = TransitionMath.TalkStartInSong(
                    introEnd, outgoing.DurationSeconds, settings.PostHitSafetyMs);
                return new TransitionPlan(strategy, outgoing.DurationSeconds, 0,
                    talkStart, settings.DuckLevelDb, trace);

            case MixStrategy.OutroTalkOver:
                var outro = outgoing.Analysis!.OutroStartSeconds!.Value;
                var talkOverlap = Math.Min(outgoing.DurationSeconds - outro, incoming.DurationSeconds);
                return new TransitionPlan(strategy, talkOverlap, 0, null, settings.DuckLevelDb, trace);

            default:
                return new TransitionPlan(MixStrategy.HardCut, 0, 0, null, settings.DuckLevelDb, trace);
        }
    }

    internal static IReadOnlyDictionary<MixStrategy, int> ResolveWeights(string weightsJson, PairKind pairKind)
    {
        if (!string.IsNullOrWhiteSpace(weightsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(weightsJson);
                if (parsed is not null && parsed.TryGetValue(pairKind.ToString(), out var table))
                {
                    var result = new Dictionary<MixStrategy, int>();
                    foreach (var (name, weight) in table)
                    {
                        if (Enum.TryParse<MixStrategy>(name, ignoreCase: true, out var strategy))
                        {
                            result[strategy] = weight;
                        }
                    }

                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
            }
            catch (JsonException)
            {
                // invalid custom table → built-in defaults
            }
        }

        return DefaultWeights[pairKind];
    }

    /// <summary>Validates a custom weights table (admin save guard).</summary>
    public static bool TryValidateWeightsJson(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true; // empty = defaults
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json);
            if (parsed is null)
            {
                error = "Weights JSON must be an object of pair kinds.";
                return false;
            }

            foreach (var (kindName, table) in parsed)
            {
                if (!Enum.TryParse<PairKind>(kindName, ignoreCase: true, out _))
                {
                    error = $"Unknown pair kind '{kindName}'.";
                    return false;
                }

                foreach (var (strategyName, weight) in table)
                {
                    if (!Enum.TryParse<MixStrategy>(strategyName, ignoreCase: true, out _))
                    {
                        error = $"Unknown strategy '{strategyName}' under '{kindName}'.";
                        return false;
                    }

                    if (weight < 0)
                    {
                        error = $"Negative weight for '{kindName}.{strategyName}'.";
                        return false;
                    }
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
