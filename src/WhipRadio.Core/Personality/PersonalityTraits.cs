using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Personality;

public enum Energy
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh,
}

public enum Formality
{
    VeryCasual,
    Casual,
    Balanced,
    Formal,
    VeryFormal,
}

public enum HumorLevel
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh,
}

public enum Talkativeness
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh,
}

public enum Warmth
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh,
}

public sealed record HostPersonalityTraits(
    Energy Energy,
    Formality Formality,
    HumorLevel HumorLevel,
    Talkativeness Talkativeness,
    Warmth Warmth)
{
    public override string ToString()
        => $"energy={Energy}, formality={Formality}, humor={HumorLevel}, talkativeness={Talkativeness}, warmth={Warmth}";
}

public enum PersonalityTraitKind
{
    Energy,
    Formality,
    HumorLevel,
    Talkativeness,
    Warmth,
}

public static class MoodEngine
{
    private const int MinTrait = 0;
    private const int MaxTrait = 4;
    private const int MaxBaselineOffset = 2;

    public static HostPersonalityTraits Baseline(Moderator moderator)
        => new(
            moderator.BaselineEnergy,
            moderator.BaselineFormality,
            moderator.BaselineHumorLevel,
            moderator.BaselineTalkativeness,
            moderator.BaselineWarmth);

    public static HostPersonalityTraits Current(Moderator moderator, DateTimeOffset localNow)
        => Current(Baseline(moderator), moderator.Id, localNow);

    public static HostPersonalityTraits Current(
        HostPersonalityTraits baseline,
        int seed,
        DateTimeOffset localNow)
    {
        var day = DateOnly.FromDateTime(localNow.DateTime);
        var hour = Math.Clamp(localNow.Hour, 0, 23);
        return new HostPersonalityTraits(
            (Energy)Drift((int)baseline.Energy, PersonalityTraitKind.Energy, seed, day, hour),
            (Formality)Drift((int)baseline.Formality, PersonalityTraitKind.Formality, seed, day, hour),
            (HumorLevel)Drift((int)baseline.HumorLevel, PersonalityTraitKind.HumorLevel, seed, day, hour),
            (Talkativeness)Drift((int)baseline.Talkativeness, PersonalityTraitKind.Talkativeness, seed, day, hour),
            (Warmth)Drift((int)baseline.Warmth, PersonalityTraitKind.Warmth, seed, day, hour));
    }

    public static HostPersonalityTraits InferBaseline(string style, double talkativeness)
    {
        var normalized = style.ToLowerInvariant();
        var highEnergy = ContainsAny(normalized, "fast", "energetic", "bubbly", "party", "chatty");
        var lowEnergy = ContainsAny(normalized, "slow", "calm", "laid-back", "laid back", "thoughtful");
        var humorous = ContainsAny(normalized, "dry", "pun", "funny", "humor", "comedy", "witty");
        var formal = ContainsAny(normalized, "formal", "measured", "thoughtful", "classic");
        var casual = ContainsAny(normalized, "casual", "laid-back", "laid back", "beach", "easy");
        var warm = ContainsAny(normalized, "warm", "friendly", "bubbly", "late-night", "late night");

        return new HostPersonalityTraits(
            highEnergy ? Energy.High : lowEnergy ? Energy.Low : Energy.Medium,
            formal ? Formality.Formal : casual ? Formality.Casual : Formality.Balanced,
            humorous ? HumorLevel.High : HumorLevel.Medium,
            ToTalkativenessTrait(talkativeness),
            warm ? Warmth.High : Warmth.Medium);
    }

    public static int DistanceFromBaseline(int baseline, int current)
        => Math.Abs(current - baseline);

    public static int TraitDistance(HostPersonalityTraits left, HostPersonalityTraits right, PersonalityTraitKind trait)
        => Math.Abs(GetOrdinal(left, trait) - GetOrdinal(right, trait));

    public static int GetOrdinal(HostPersonalityTraits traits, PersonalityTraitKind trait)
        => trait switch
        {
            PersonalityTraitKind.Energy => (int)traits.Energy,
            PersonalityTraitKind.Formality => (int)traits.Formality,
            PersonalityTraitKind.HumorLevel => (int)traits.HumorLevel,
            PersonalityTraitKind.Talkativeness => (int)traits.Talkativeness,
            PersonalityTraitKind.Warmth => (int)traits.Warmth,
            _ => throw new ArgumentOutOfRangeException(nameof(trait), trait, null),
        };

    private static int Drift(
        int baseline,
        PersonalityTraitKind trait,
        int seed,
        DateOnly day,
        int currentHour)
    {
        var current = baseline;
        for (var hour = 0; hour <= currentHour; hour++)
        {
            var targetOffset = TimeBias(trait, hour);
            if (targetOffset == 0)
            {
                targetOffset = NeutralNudge(seed, day, hour, trait);
            }

            var target = ClampTrait(baseline + targetOffset, baseline);
            current = MoveOneStep(current, target);
        }

        return current;
    }

    private static int TimeBias(PersonalityTraitKind trait, int hour)
    {
        var lateNight = hour is <= 4 or >= 22;
        var driveTime = hour is >= 6 and <= 9 or >= 16 and <= 18;
        var evening = hour is >= 19 and <= 21;

        return trait switch
        {
            PersonalityTraitKind.Energy when lateNight => -1,
            PersonalityTraitKind.Energy when driveTime => 1,
            PersonalityTraitKind.HumorLevel when lateNight => -1,
            PersonalityTraitKind.HumorLevel when driveTime => 1,
            PersonalityTraitKind.Talkativeness when lateNight => -1,
            PersonalityTraitKind.Talkativeness when driveTime => 1,
            PersonalityTraitKind.Formality when lateNight => 1,
            PersonalityTraitKind.Formality when driveTime => -1,
            PersonalityTraitKind.Warmth when driveTime || evening => 1,
            _ => 0,
        };
    }

    private static int NeutralNudge(int seed, DateOnly day, int hour, PersonalityTraitKind trait)
    {
        unchecked
        {
            var hash = seed == 0 ? 17 : seed;
            hash = (hash * 397) ^ day.DayNumber;
            hash = (hash * 397) ^ hour;
            hash = (hash * 397) ^ (int)trait;
            var roll = (hash & int.MaxValue) % 24;
            return roll switch
            {
                0 => -1,
                1 => 1,
                _ => 0,
            };
        }
    }

    private static int MoveOneStep(int current, int target)
    {
        if (current < target)
        {
            return current + 1;
        }

        if (current > target)
        {
            return current - 1;
        }

        return current;
    }

    private static int ClampTrait(int value, int baseline)
        => Math.Clamp(value, Math.Max(MinTrait, baseline - MaxBaselineOffset), Math.Min(MaxTrait, baseline + MaxBaselineOffset));

    private static Talkativeness ToTalkativenessTrait(double talkativeness)
        => Math.Clamp(talkativeness, 0, 1) switch
        {
            < 0.2 => Talkativeness.VeryLow,
            < 0.4 => Talkativeness.Low,
            > 0.8 => Talkativeness.VeryHigh,
            > 0.6 => Talkativeness.High,
            _ => Talkativeness.Medium,
        };

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(value.Contains);
}
