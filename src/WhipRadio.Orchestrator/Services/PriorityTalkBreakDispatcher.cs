using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class PriorityTalkBreakDispatcher(
    IDbContextFactory<RadioDbContext> dbFactory,
    IPlayoutQueue playoutQueue,
    QueueStateTracker queueTracker,
    TimeProvider timeProvider,
    ILogger<PriorityTalkBreakDispatcher> logger)
{
    private readonly HashSet<Guid> _frontPushedAnnouncementIds = [];

    public async Task<int> PushReadyAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        List<TalkBreak> candidates;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candidates = await db.TalkBreaks.AsNoTracking()
                .Where(talkBreak => talkBreak.AnnouncementId != null)
                .Include(talkBreak => talkBreak.Parts)
                .ToListAsync(ct);

            var candidateAnnouncementIds = candidates
                .Where(talkBreak => TalkBreakPriorityPolicy.IsOnDemandPriority(talkBreak, now))
                .Select(talkBreak => talkBreak.AnnouncementId!.Value)
                .ToList();
            if (candidateAnnouncementIds.Count == 0)
            {
                _frontPushedAnnouncementIds.Clear();
                return 0;
            }

            var unplayedAnnouncementIds = await db.Announcements.AsNoTracking()
                .Where(announcement => candidateAnnouncementIds.Contains(announcement.Id) && !announcement.WasPlayed)
                .Select(announcement => announcement.Id)
                .ToHashSetAsync(ct);

            candidates = candidates
                .Where(talkBreak => talkBreak.AnnouncementId is { } announcementId
                    && unplayedAnnouncementIds.Contains(announcementId)
                    && TalkBreakPriorityPolicy.IsOnDemandPriority(talkBreak, now))
                .ToList();
        }

        var candidateIds = candidates.Select(talkBreak => talkBreak.AnnouncementId!.Value).ToHashSet();
        _frontPushedAnnouncementIds.RemoveWhere(id => !candidateIds.Contains(id));

        var queuedIds = queueTracker.Snapshot()
            .Where(item => item.ItemType == PlayoutItemType.Announcement)
            .Select(item => item.ItemId)
            .ToHashSet();

        var pushed = 0;
        foreach (var talkBreak in TalkBreakPriorityPolicy.OrderForFrontPush(candidates))
        {
            var announcementId = talkBreak.AnnouncementId!.Value;
            if (queuedIds.Contains(announcementId) || !_frontPushedAnnouncementIds.Add(announcementId))
            {
                continue;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var announcement = await db.Announcements.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == announcementId && !item.WasPlayed, ct);
            if (announcement is null)
            {
                continue;
            }

            var moderator = await db.Moderators.AsNoTracking()
                .FirstOrDefaultAsync(host => host.Id == announcement.ModeratorId, ct);
            if (moderator is null)
            {
                continue;
            }

            playoutQueue.EnqueueFront(ToPlayoutItem(announcement, moderator));
            pushed++;
            logger.LogInformation(
                "{Priority} talk break {TalkBreakId} jumps to the front of the queue",
                talkBreak.Priority,
                talkBreak.Id);
        }

        return pushed;
    }

    private static PlayoutItem ToPlayoutItem(Announcement announcement, Moderator moderator) => new(
        PlayoutItemType.Announcement,
        announcement.Id,
        announcement.FilePath,
        $"{announcement.Kind} - {moderator.Name}",
        announcement.DurationSeconds,
        moderator.Id);
}
