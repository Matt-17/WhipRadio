using WhipRadio.Core.Api;

namespace WhipRadio.Web.Components.Moderators;

/// <summary>Display formatting shared by the hosts page and its child components.</summary>
internal static class HostFormat
{
    public static string TalkLabel(double talkativeness) => talkativeness switch
    {
        < 0.35 => "quiet",
        > 0.65 => "talky",
        _ => "balanced",
    };

    public static string TraitLabel(string value)
        => value switch
        {
            "VeryLow" => "very low",
            "VeryHigh" => "very high",
            "VeryCasual" => "very casual",
            "VeryFormal" => "very formal",
            _ => value.ToLowerInvariant(),
        };

    public static string MoodSummary(ModeratorTraitsDto? traits)
        => traits is null ? "mood pending" : $"{TraitLabel(traits.Energy)} energy";

    public static string BreakFrequencyLabel(int frequency)
        => frequency <= 0 ? "music only" : frequency == 1 ? "every track" : $"every {frequency}";

    public static IReadOnlyList<string> FireClearedItems(ModeratorUsageDto usage)
    {
        var items = new List<string>();
        if (usage.IsNewsPresenter)
        {
            items.Add("News presenter assignment");
        }

        if (usage.IsWeatherSpecialist)
        {
            items.Add("Weather specialist assignment");
        }

        AddCount(items, usage.AssignedFormatCount, "show format assignment");
        AddCount(items, usage.ActiveTalkBitCount, "active reusable talk bit");
        AddCount(items, usage.PendingTalkBreakCount, "pending talk");
        AddCount(items, usage.AssignedListenerMessageCount, "assigned listener message");
        return items;
    }

    public static string FireHistoryText(ModeratorUsageDto usage)
        => $"History kept: {CountText(usage.HistoricalAnnouncementCount, "archived talk")}, "
            + $"{CountText(usage.HistoricalPlayCount, "logged play")}.";

    private static void AddCount(List<string> items, int count, string singular)
    {
        if (count > 0)
        {
            items.Add($"{count} {singular}{(count == 1 ? "" : "s")}");
        }
    }

    private static string CountText(int count, string singular)
        => $"{count} {singular}{(count == 1 ? "" : "s")}";
}
