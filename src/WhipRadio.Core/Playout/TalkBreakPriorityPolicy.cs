using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Playout;

public static class TalkBreakPriorityPolicy
{
    public static bool IsOnDemandPriority(TalkBreak talkBreak, DateTime utcNow)
        => talkBreak.Status == TalkBreakStatus.Rendered
            && talkBreak.AnnouncementId is not null
            && talkBreak.Priority is TalkBreakPriority.High or TalkBreakPriority.Emergency
            && (talkBreak.ExpiresAtUtc is null || talkBreak.ExpiresAtUtc > utcNow);

    public static IReadOnlyList<TalkBreak> OrderForFrontPush(IEnumerable<TalkBreak> talkBreaks)
        => talkBreaks
            .OrderBy(PriorityRank)
            .ThenByDescending(talkBreak => talkBreak.CreatedAtUtc)
            .ToList();

    public static int PriorityRank(TalkBreak talkBreak)
        => talkBreak.Priority switch
        {
            TalkBreakPriority.Emergency => 2,
            TalkBreakPriority.High => 1,
            _ => 0,
        };
}
