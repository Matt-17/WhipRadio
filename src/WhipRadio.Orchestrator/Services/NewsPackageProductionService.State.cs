using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class NewsPackageProductionService
{
    private static async Task ExpireOldCompositeAsync(RadioDbContext db, Guid oldAnnouncementId, CancellationToken ct)
    {
        var oldBreaks = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.AnnouncementId == oldAnnouncementId)
            .ToListAsync(ct);
        foreach (var talkBreak in oldBreaks)
        {
            talkBreak.Status = TalkBreakStatus.Expired;
            foreach (var part in talkBreak.Parts)
            {
                part.Status = TalkPartStatus.Expired;
            }
        }

        // Mark the old announcement as played so it won't be picked up by any
        // Immediate-playable query (WasPlayed filter).
        var oldAnnouncement = await db.Announcements.FirstOrDefaultAsync(a => a.Id == oldAnnouncementId, ct);
        if (oldAnnouncement is not null)
        {
            oldAnnouncement.WasPlayed = true;
            oldAnnouncement.PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<NewsPackage?> LoadPackageAsync(Guid packageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NewsPackages.AsNoTracking().FirstOrDefaultAsync(package => package.Id == packageId, ct);
    }

    private async Task MarkScheduledAsync(
        IReadOnlyList<Guid> announcementIds,
        DateTime targetUtc,
        DateTime targetEndUtc,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var breaks = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.AnnouncementId != null
                && announcementIds.Contains(talkBreak.AnnouncementId.Value))
            .ToListAsync(ct);
        foreach (var talkBreak in breaks)
        {
            talkBreak.Priority = TalkBreakPriority.Scheduled;
            talkBreak.TargetWindowStartUtc = targetUtc;
            talkBreak.TargetWindowEndUtc = targetEndUtc;
            talkBreak.ExpiresAtUtc = expiresAtUtc;
            foreach (var part in talkBreak.Parts)
            {
                part.Priority = TalkBreakPriority.Scheduled;
                part.TargetWindowStartUtc = targetUtc;
                part.TargetWindowEndUtc = targetEndUtc;
                part.ExpiresAtUtc = expiresAtUtc;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task FinalizePackageAsync(
        Guid packageId,
        Announcement composite,
        DateTime targetUtc,
        DateTime targetEndUtc,
        DateTime expiresAtUtc,
        IReadOnlyList<NewsItem> items,
        PackagePlan plan,
        IReadOnlyList<string> degradationReasons,
        CancellationToken ct)
    {
        var isMultiSegment = plan.Segments.Count > 1;
        var singleLabel = plan.Segments.Count == 1 ? plan.Segments[0].Label : null;

        var (kind, purpose, title) = isMultiSegment
            ? (AnnouncementKind.News, "TopOfHourPackage", "Top of hour")
            : singleLabel is not null
                ? (singleLabel.Kind, singleLabel.Purpose, singleLabel.Title)
                : (AnnouncementKind.News, "TopOfHourPackage", "Top of hour");

        var hasDegradation = degradationReasons.Count > 0;
        var sourceSummary = BuildSourceSummary(plan, items, degradationReasons);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstAsync(p => p.Id == packageId, ct);
        package.Status = NewsPackageStatus.Ready;
        package.AnnouncementId = composite.Id;
        package.ProducedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        package.FailureReason = hasDegradation
            ? string.Join("; ", degradationReasons) is { Length: > 0 } reason
                ? (reason.Length <= 1000 ? reason : reason[..1000])
                : null
            : null;
        package.ProductionState = hasDegradation ? "Ready with degradation." : null;
        package.SourceSummary = sourceSummary;

        var announcement = await db.Announcements.FirstAsync(a => a.Id == composite.Id, ct);
        announcement.Kind = kind;
        announcement.PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly;

        var talkBreak = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .FirstAsync(talkBreak => talkBreak.AnnouncementId == composite.Id, ct);
        talkBreak.Priority = TalkBreakPriority.Scheduled;
        talkBreak.Purpose = purpose;
        talkBreak.Title = title;
        talkBreak.TargetWindowStartUtc = targetUtc;
        talkBreak.TargetWindowEndUtc = targetEndUtc;
        talkBreak.ExpiresAtUtc = expiresAtUtc;
        foreach (var part in talkBreak.Parts)
        {
            part.Priority = TalkBreakPriority.Scheduled;
            part.TargetWindowStartUtc = targetUtc;
            part.TargetWindowEndUtc = targetEndUtc;
            part.ExpiresAtUtc = expiresAtUtc;
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            item.Status = NewsItemStatus.Produced;
            item.ProducedAtUtc = package.ProducedAtUtc;
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private static string BuildSourceSummary(
        PackagePlan plan,
        IReadOnlyList<NewsItem> items,
        IReadOnlyList<string> degradationReasons)
    {
        var hasNewsItems = items.Count > 0;
        var hasDegradation = degradationReasons.Count > 0;

        if (plan.Segments.Count > 1)
        {
            var newsSummary = hasNewsItems
                ? string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"))
                : "news unavailable";
            var weatherSummary = plan.Segments.Any(s => s.Key == "weather") ? "Weather forecast" : "";
            var parts = new List<string>();
            if (plan.Segments.Any(s => s.Key == "news"))
            {
                parts.Add(hasNewsItems ? newsSummary : "News unavailable");
            }
            if (!string.IsNullOrEmpty(weatherSummary))
            {
                parts.Add(weatherSummary);
            }
            var summary = string.Join("; ", parts);
            return hasDegradation ? $"{summary} (with degradation)" : summary;
        }

        if (plan.Segments.Count == 1)
        {
            return plan.Segments[0].Key == "news" && hasNewsItems
                ? string.Join("; ", items.Select(item => $"{item.Feed?.Label}: {item.Title}"))
                : plan.Segments[0].Key == "news"
                    ? "News update (no items available)"
                    : "Weather forecast";
        }

        return string.Empty;
    }

    private async Task UpdatePackageProductionStateAsync(
        Guid packageId,
        string state,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null || package.Status is not (NewsPackageStatus.Pending or NewsPackageStatus.Retrying))
        {
            return;
        }

        package.Status = NewsPackageStatus.Pending;
        package.ProductionState = state.Length <= 500 ? state : state[..500];
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    /// <summary>
    /// Advances the high-level step counter (and production text) shown as "k/N" in the
    /// Production page. Fine-grained, per-contributor progress goes through
    /// <see cref="UpdatePackageProductionStateAsync"/>, which keeps the current step number.
    /// </summary>
    private async Task UpdateStepAsync(
        Guid packageId,
        int index,
        int total,
        string state,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null || package.Status is not (NewsPackageStatus.Pending or NewsPackageStatus.Retrying))
        {
            return;
        }

        package.Status = NewsPackageStatus.Pending;
        package.StepIndex = index;
        package.StepTotal = total;
        package.ProductionState = state.Length <= 500 ? state : state[..500];
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task PersistSegmentsAsync(
        Guid packageId,
        IReadOnlyList<NewsPackageSegmentState> segments,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return;
        }

        package.ProducedSegmentsJson = SerializeSegments(segments);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-loads an already-produced segment's announcements, host, and news items so a resumed
    /// run can re-attach them. Returns null when the saved audio is missing (the caller then
    /// re-produces the segment).
    /// </summary>

    private static async Task ExpireSegmentAnnouncementsAsync(
        RadioDbContext db,
        IReadOnlyCollection<NewsPackageSegmentState> segments,
        CancellationToken ct)
    {
        var ids = segments
            .SelectMany(segment => new[]
            {
                (Guid?)segment.IntroAnnouncementId,
                segment.BodyAnnouncementId,
                segment.GapLineAnnouncementId,
                segment.OutroAnnouncementId,
            })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var breaks = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.AnnouncementId != null && ids.Contains(talkBreak.AnnouncementId.Value))
            .ToListAsync(ct);
        foreach (var talkBreak in breaks)
        {
            talkBreak.Status = TalkBreakStatus.Expired;
            foreach (var part in talkBreak.Parts)
            {
                part.Status = TalkPartStatus.Expired;
            }
        }

        foreach (var announcement in await db.Announcements.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            announcement.WasPlayed = true;
            announcement.PlayoutIntent = AnnouncementPlayoutIntent.ScheduledOnly;
        }
    }

    private static List<NewsPackageSegmentState> DeserializeSegments(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<NewsPackageSegmentState>>(json) ?? [];

    private static string SerializeSegments(IReadOnlyList<NewsPackageSegmentState> segments)
        => JsonSerializer.Serialize(segments);

    /// <summary>
    /// Run one planned segment: fan its draft jobs out (so all writes queue together), voice
    /// each as its script lands, then persist the segment for resume once the handover aired.
    /// </summary>

    private async Task MarkPackageFailedAsync(
        Guid packageId,
        string reason,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is not null)
        {
            package.Status = NewsPackageStatus.Failed;
            package.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
            package.ProductionState = null;
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            if (item.Status == NewsItemStatus.Selected)
            {
                item.Status = NewsItemStatus.New;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }

    private async Task MarkPackageStoppedAsync(
        Guid packageId,
        string reason,
        IReadOnlyList<NewsItem> items,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var package = await db.NewsPackages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is not null && package.Status is NewsPackageStatus.Pending or NewsPackageStatus.Retrying)
        {
            package.Status = NewsPackageStatus.Retrying;
            package.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
            package.ProductionState = "Stopped. Waiting for the production service to retry.";
        }

        var itemIds = items.Select(item => item.Id).ToList();
        foreach (var item in await db.NewsItems.Where(item => itemIds.Contains(item.Id)).ToListAsync(ct))
        {
            if (item.Status == NewsItemStatus.Selected)
            {
                item.Status = NewsItemStatus.New;
            }
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
    }
}
