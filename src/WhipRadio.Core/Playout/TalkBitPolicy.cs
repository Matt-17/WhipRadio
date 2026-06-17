using System.Text.RegularExpressions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Playout;

public static partial class TalkBitPolicy
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "and", "because", "before", "der", "die", "das",
        "ein", "eine", "for", "from", "mit", "oder", "the", "und", "with",
    };

    public static bool IsEligible(TalkBit bit, DateTime utcNow)
        => bit.Status == TalkBitStatus.Active
            && (bit.LastUsedAtUtc is null
                || bit.LastUsedAtUtc.Value.AddDays(Math.Max(0, bit.CooldownDays)) <= utcNow);

    public static double SelectionWeight(TalkBit bit, DateTime utcNow)
    {
        if (!IsEligible(bit, utcNow))
        {
            return 0;
        }

        var ageBoost = bit.LastUsedAtUtc is null
            ? 1.5
            : Math.Min(2.0, Math.Max(1.0, (utcNow - bit.LastUsedAtUtc.Value).TotalDays / Math.Max(1, bit.CooldownDays)));
        return ageBoost / (1 + bit.PlayCount);
    }

    public static TalkBit? PickWeighted(IEnumerable<TalkBit> bits, DateTime utcNow, Random random)
    {
        var weighted = bits
            .Select(bit => new { Bit = bit, Weight = SelectionWeight(bit, utcNow) })
            .Where(entry => entry.Weight > 0)
            .ToList();
        var total = weighted.Sum(entry => entry.Weight);
        if (total <= 0)
        {
            return null;
        }

        var roll = random.NextDouble() * total;
        foreach (var entry in weighted)
        {
            roll -= entry.Weight;
            if (roll <= 0)
            {
                return entry.Bit;
            }
        }

        return weighted[^1].Bit;
    }

    public static bool ShouldForceRetelling(TalkBit bit, int exactReplayLimit = 2)
        => bit.ExactReplayCount >= Math.Max(0, exactReplayLimit);

    public static bool ShouldRetire(TalkBit bit, int maxPlayCount = 12, int maxAgeDays = 180, DateTime? utcNow = null)
    {
        if (bit.Status == TalkBitStatus.Retired)
        {
            return true;
        }

        var now = utcNow ?? DateTime.UtcNow;
        return bit.PlayCount >= maxPlayCount || bit.CreatedAtUtc.AddDays(maxAgeDays) <= now;
    }

    public static bool LooksDuplicate(string premise, IEnumerable<TalkBit> existingBits, double overlapThreshold = 0.6)
    {
        var keywords = ExtractKeywords(premise);
        if (keywords.Count == 0)
        {
            return false;
        }

        foreach (var bit in existingBits.Where(bit => bit.Status == TalkBitStatus.Active))
        {
            var existing = ExtractKeywords(bit.Premise);
            if (existing.Count == 0)
            {
                continue;
            }

            var overlap = keywords.Intersect(existing, StringComparer.OrdinalIgnoreCase).Count();
            var denominator = Math.Min(keywords.Count, existing.Count);
            if (denominator > 0 && (double)overlap / denominator >= overlapThreshold)
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlySet<string> ExtractKeywords(string text)
        => WordRegex().Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => word.Length >= 4 && !StopWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
