using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class NewsPackageProductionService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    ScheduleService schedule,
    IEnumerable<ITopOfHourSegmentContributor> contributors,
    TimedPlayoutInterruptService timedInterrupts,
    TimeProvider timeProvider,
    IProductionUpdatePublisher productionUpdates,
    IStationMetrics metrics,
    ILogger<NewsPackageProductionService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProductionBudget = TimeSpan.FromMinutes(20);

    // Package ids currently being produced by this (singleton) service. A manual
    // RecreatePackageAsync sets its package to Pending and produces it inline; without
    // this guard the background loop (TryResumePendingPackageAsync / RunCycleAsync) would
    // see that same Pending package and produce it a SECOND time concurrently, rendering a
    // rival composite that can reach the mixer's timed interrupt alongside the real one —
    // an old/second news airing in parallel at the top of the hour.
    private readonly ConcurrentDictionary<Guid, byte> _producing = new();

    internal sealed record PackagePlan(
        DateTimeOffset TargetLocal,
        IReadOnlyList<ITopOfHourSegmentContributor> Segments);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            const string kind = "news";
            var cycleStart = Stopwatch.GetTimestamp();
            try
            {
                await RunCycleAsync(stoppingToken);
                metrics.GenerationSucceeded(kind, Stopwatch.GetElapsedTime(cycleStart));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.GenerationFailed(kind);
                logger.LogError(ex, "News package production cycle failed");
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        StationSettings settings;
        PackagePlan? plan;
        Guid? resumePackageId = null;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!contributors.Any(c => c.IsEnabled(settings)))
            {
                return;
            }

            if (await TryResumePendingPackageAsync(settings, ct))
            {
                return;
            }

            plan = ResolveNextPreparationPlan(settings, timeProvider.GetLocalNow());
            if (plan is null)
            {
                return;
            }

            var targetUtc = plan.TargetLocal.UtcDateTime;
            var existing = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour && package.TargetUtc == targetUtc)
                .Select(package => new { package.Id, package.Status })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                // A recreate (or another resume) is already producing this package inline —
                // never start a rival production for it.
                if (_producing.ContainsKey(existing.Id))
                {
                    return;
                }

                // A Ready/Queued/Played package already owns this slot — leave it.
                if (existing.Status is not (NewsPackageStatus.Failed
                    or NewsPackageStatus.Pending
                    or NewsPackageStatus.Retrying))
                {
                    return;
                }

                // A leftover incomplete/failed package for an upcoming target: re-attempt it,
                // reusing whatever segments were already produced. This covers a restart that
                // lands inside the prep window (after the prepare point, before air time) —
                // production must pick back up, not stall.
                resumePackageId = existing.Id;
            }
        }

        // Claim the package id so a concurrent recreate/resume can't double-produce it.
        if (resumePackageId is { } claimId && !_producing.TryAdd(claimId, 0))
        {
            return;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ProductionBudget);
            await ProducePackageAsync(
                settings,
                plan.TargetLocal.UtcDateTime,
                plan,
                ct,
                budget.Token,
                reusePackageId: resumePackageId,
                reuseSegments: resumePackageId is not null);
        }
        finally
        {
            if (resumePackageId is { } releaseId)
            {
                _producing.TryRemove(releaseId, out _);
            }
        }
    }

    private async Task<bool> TryResumePendingPackageAsync(StationSettings settings, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldestValidTarget = now.AddSeconds(
            -TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var expired = await db.NewsPackages
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying)
                    && package.TargetUtc < oldestValidTarget)
                .ToListAsync(ct);
            foreach (var package in expired)
            {
                // Expire any segment audio already produced so it can never be picked up
                // independently of its (now failed) package.
                await ExpireSegmentAnnouncementsAsync(db, DeserializeSegments(package.ProducedSegmentsJson), ct);
                package.Status = NewsPackageStatus.Failed;
                package.FailureReason = "Production did not finish before the top-of-hour late window.";
                package.ProductionState = null;
                package.ProducedSegmentsJson = null;
                package.StepIndex = 0;
                package.StepTotal = 0;
            }

            if (expired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                await productionUpdates.PublishNewsChangedAsync(ct);
            }

            // Skip any package a recreate/resume is already producing inline.
            var producingIds = _producing.Keys.ToList();
            var pending = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying)
                    && package.TargetUtc >= oldestValidTarget
                    && !producingIds.Contains(package.Id))
                .OrderBy(package => package.TargetUtc)
                .FirstOrDefaultAsync(ct);
            if (pending is null)
            {
                return false;
            }

            // Claim it so the cycle's own resume path (and any other thread) can't re-enter.
            if (!_producing.TryAdd(pending.Id, 0))
            {
                return false;
            }

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ProductionBudget);
                var plan = BuildPackagePlan(settings, ToLocalTime(pending.TargetUtc, timeProvider.GetLocalNow().Offset));
                await ProducePackageAsync(settings, pending.TargetUtc, plan, ct, budget.Token, pending.Id, reuseSegments: true);
                return true;
            }
            finally
            {
                _producing.TryRemove(pending.Id, out _);
            }
        }
    }

    public async Task<NewsPackage?> ProduceNextPackageAsync(CancellationToken ct)
    {
        StationSettings settings;
        PackagePlan plan;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            plan = ResolveNextPackagePlan(settings, timeProvider.GetLocalNow());
            var targetUtc = plan.TargetLocal.UtcDateTime;

            var existing = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc == targetUtc
                    && package.Status != NewsPackageStatus.Failed)
                .OrderByDescending(package => package.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        return await ProducePackageAsync(settings, plan.TargetLocal.UtcDateTime, plan, ct, budget.Token);
    }

    public async Task<NewsPackage?> RecreatePackageAsync(Guid packageId, CancellationToken ct)
    {
        // Claim the package before touching it so the background loop can't grab it as a
        // "Pending" resume and produce a rival composite in parallel (double news on air).
        if (!_producing.TryAdd(packageId, 0))
        {
            return await LoadPackageAsync(packageId, ct);
        }

        try
        {
            return await RecreatePackageCoreAsync(packageId, ct);
        }
        finally
        {
            _producing.TryRemove(packageId, out _);
        }
    }

    private async Task<NewsPackage?> RecreatePackageCoreAsync(Guid packageId, CancellationToken ct)
    {
        StationSettings settings;
        DateTime targetUtc;
        Guid? oldAnnouncementId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            var package = await db.NewsPackages.FirstOrDefaultAsync(candidate => candidate.Id == packageId, ct)
                ?? throw new KeyNotFoundException("News package was not found.");
            if (package.Status is NewsPackageStatus.Queued or NewsPackageStatus.Played)
            {
                throw new InvalidOperationException("Queued or played packages cannot be recreated.");
            }

            targetUtc = package.TargetUtc;
            oldAnnouncementId = package.AnnouncementId;
            var oldSegments = DeserializeSegments(package.ProducedSegmentsJson);

            // Mark the package as Pending immediately so the dispatcher (1s cycle) cannot
            // race in and queue/schedule the OLD composite during recreate production.
            package.Status = NewsPackageStatus.Pending;
            package.AnnouncementId = null;
            package.ProductionState = "Recreating package.";
            package.FailureReason = null;
            // A recreate is a deliberate fresh start: drop the persisted segments so production
            // re-writes everything, and expire the old segment audio so it can't air on its own.
            package.ProducedSegmentsJson = null;
            package.StepIndex = 0;
            package.StepTotal = 0;

            // Reset selected news items so the new production can re-select them.
            foreach (var item in await db.NewsItems
                .Where(item => item.Status == NewsItemStatus.Selected
                    && item.SelectionReason == "Top-of-hour package")
                .ToListAsync(ct))
            {
                item.Status = NewsItemStatus.New;
                item.SelectionReason = null;
            }

            await db.SaveChangesAsync(ct);

            // Expire the OLD composite announcement's TalkBreak and TalkParts so no path
            // (gap-talk weather, priority dispatcher, ShowRunner) can find or play it.
            if (oldAnnouncementId is { } oldId)
            {
                await ExpireOldCompositeAsync(db, oldId, ct);
            }

            // Expire the OLD per-segment intros/bodies/gap lines too, otherwise they linger
            // as a "second package" that can air independently of the recreated composite.
            if (oldSegments.Count > 0)
            {
                await ExpireSegmentAnnouncementsAsync(db, oldSegments, ct);
                await db.SaveChangesAsync(ct);
            }
        }

        // Clear any pending timed interrupt that references the old composite so the
        // mixer doesn't play it at the target time.
        timedInterrupts.Clear();

        await productionUpdates.PublishNewsChangedAsync(ct);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProductionBudget);
        return await ProducePackageAsync(settings, targetUtc, plan: BuildPackagePlan(settings, ToLocalTime(targetUtc, timeProvider.GetLocalNow().Offset)), ct, budget.Token, packageId);
    }

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

    private async Task<NewsPackage?> ProducePackageAsync(
        StationSettings settings,
        DateTime targetUtc,
        PackagePlan plan,
        CancellationToken stoppingToken,
        CancellationToken ct,
        Guid? reusePackageId = null,
        bool reuseSegments = false)
    {
        var includedContributors = plan.Segments;
        // High-level steps surfaced as "k/N": load context (1) + one per segment + schedule + render + finalize.
        var totalSteps = includedContributors.Count + 4;
        NewsPackage package;
        List<NewsPackageSegmentState> producedSegments;
        var step = "creating package";
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (reusePackageId is { } packageId)
            {
                package = await db.NewsPackages.FirstOrDefaultAsync(candidate => candidate.Id == packageId, ct)
                    ?? throw new KeyNotFoundException("News package was not found.");
                producedSegments = reuseSegments ? DeserializeSegments(package.ProducedSegmentsJson) : [];
                package.Kind = NewsPackageKind.TopOfHour;
                package.Status = NewsPackageStatus.Pending;
                package.TargetUtc = targetUtc;
                package.TargetDurationSeconds = TargetDurationSeconds(settings, plan);
                package.AnnouncementId = null;
                package.ProducedAtUtc = null;
                package.QueuedAtUtc = null;
                package.PlayedAtUtc = null;
                package.FailureReason = null;
                package.ProductionState = "Starting package production.";
                package.SourceSummary = null;
                // Keep already-produced segments when resuming; drop them on a deliberate fresh run.
                package.ProducedSegmentsJson = reuseSegments ? package.ProducedSegmentsJson : null;
                package.StepIndex = 0;
                package.StepTotal = totalSteps;
            }
            else
            {
                producedSegments = [];
                package = new NewsPackage
                {
                    Id = Guid.NewGuid(),
                    Kind = NewsPackageKind.TopOfHour,
                    Status = NewsPackageStatus.Pending,
                    TargetUtc = targetUtc,
                    TargetDurationSeconds = TargetDurationSeconds(settings, plan),
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                    ProductionState = "Starting package production.",
                    StepTotal = totalSteps,
                };
                db.NewsPackages.Add(package);
            }
            await db.SaveChangesAsync(ct);
        }
        await productionUpdates.PublishNewsChangedAsync(ct);

        List<NewsItem> allItems = [];
        var degradationReasons = new List<string>();
        var producedAnnouncements = new List<Announcement>();
        Moderator? firstSegmentHost = null;
        Moderator? fallbackModerator = null;

        try
        {
            step = "loading current show context";
            await UpdateStepAsync(package.Id, 1, totalSteps, "Loading show context.", ct);
            var context = await schedule.GetCurrentAsync(ct);

            var expiresAt = targetUtc.AddMinutes(15);
            var targetEnd = targetUtc.AddSeconds(
                TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds));
            var localNow = timeProvider.GetLocalNow();
            var targetLocal = new DateTimeOffset(
                DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc),
                TimeSpan.Zero).ToOffset(localNow.Offset);

            using var scope = scopeFactory.CreateScope();
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();

            Moderator? previousSegmentHost = null;
            for (var i = 0; i < includedContributors.Count; i++)
            {
                var contributor = includedContributors[i];
                var stepNo = 2 + i;
                var position = i == 0
                    ? SegmentPosition.First
                    : i == includedContributors.Count - 1
                        ? SegmentPosition.Last
                        : SegmentPosition.Middle;

                // Resume: if this segment was already written + rendered on an earlier run,
                // re-attach its audio instead of re-producing it.
                var saved = producedSegments.FirstOrDefault(s => s.Key == contributor.Key && s.Done);
                if (saved is not null)
                {
                    var reused = await TryLoadSavedSegmentAsync(saved, ct);
                    if (reused is not null)
                    {
                        await UpdateStepAsync(package.Id, stepNo, totalSteps, $"Reusing {contributor.Key} segment.", ct);
                        firstSegmentHost ??= reused.Host;
                        fallbackModerator ??= reused.Host;
                        producedAnnouncements.Add(reused.Intro);
                        if (reused.Body is not null)
                        {
                            producedAnnouncements.Add(reused.Body);
                        }
                        if (reused.GapLine is not null)
                        {
                            producedAnnouncements.Add(reused.GapLine);
                        }
                        allItems.AddRange(reused.Items);
                        if (saved.DegradationReason is not null)
                        {
                            degradationReasons.Add(saved.DegradationReason);
                        }
                        previousSegmentHost = reused.Host;
                        continue;
                    }

                    // The saved audio is gone — forget it and re-produce.
                    producedSegments.RemoveAll(s => s.Key == contributor.Key);
                }

                step = $"producing {contributor.Key} segment";
                await UpdateStepAsync(package.Id, stepNo, totalSteps, $"Producing {contributor.Key} segment.", ct);

                var segmentContext = new SegmentProductionContext(
                    settings,
                    targetLocal,
                    targetUtc,
                    expiresAt,
                    context.Moderator,
                    position,
                    previousSegmentHost,
                    scope.ServiceProvider,
                    (state, token) => UpdatePackageProductionStateAsync(package.Id, state, token));

                SegmentResult? result = null;
                try
                {
                    result = await contributor.ProduceAsync(segmentContext, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    metrics.GenerationFailed("news");
                    logger.LogError(
                        ex,
                        "Top-of-hour {Key} contributor failed: {Message}",
                        contributor.Key,
                        ex.GetBaseException().Message);
                    degradationReasons.Add($"{contributor.Key} segment failed: {FailureDetail(ex)}");
                }

                if (result is not null)
                {
                    firstSegmentHost ??= result.SegmentHost;
                    fallbackModerator ??= result.SegmentHost;
                    producedAnnouncements.Add(result.Intro);
                    if (result.Body is not null)
                    {
                        producedAnnouncements.Add(result.Body);
                    }
                    if (result.GapLine is not null)
                    {
                        producedAnnouncements.Add(result.GapLine);
                    }
                    allItems.AddRange(result.SelectedItems);
                    if (result.DegradationReason is not null)
                    {
                        degradationReasons.Add(result.DegradationReason);
                    }
                    previousSegmentHost = result.SegmentHost;

                    // Persist immediately so a restart resumes from here instead of re-writing.
                    producedSegments.RemoveAll(s => s.Key == contributor.Key);
                    producedSegments.Add(new NewsPackageSegmentState
                    {
                        Key = contributor.Key,
                        Done = true,
                        IntroAnnouncementId = result.Intro.Id,
                        BodyAnnouncementId = result.Body?.Id,
                        GapLineAnnouncementId = result.GapLine?.Id,
                        SegmentHostModeratorId = result.SegmentHost.Id,
                        DegradationReason = result.DegradationReason,
                        SourceSummary = result.SourceSummary,
                        SelectedItemIds = result.SelectedItems.Select(item => item.Id).ToList(),
                    });
                    await PersistSegmentsAsync(package.Id, producedSegments, ct);
                }
            }

            if (producedAnnouncements.Count == 0)
            {
                await MarkPackageFailedAsync(
                    package.Id,
                    "Production stopped: no package audio could be produced.",
                    allItems,
                    ct);
                return await LoadPackageAsync(package.Id, CancellationToken.None);
            }

            step = "marking package announcements as scheduled";
            await UpdateStepAsync(package.Id, totalSteps - 2, totalSteps, "Scheduling package audio.", ct);
            await MarkScheduledAsync(producedAnnouncements.Select(a => a.Id).ToList(), targetUtc, targetEnd, expiresAt, ct);
            step = "rendering package audio";
            await UpdateStepAsync(package.Id, totalSteps - 1, totalSteps, "Rendering package audio.", ct);
            var fallback = fallbackModerator ?? context.Moderator;
            var composite = producedAnnouncements.Count == 1
                ? producedAnnouncements[0]
                : await renderer.RenderAsync(producedAnnouncements, fallback, ct);
            step = "finalizing package";
            await UpdateStepAsync(package.Id, totalSteps, totalSteps, "Finalizing package.", ct);
            await FinalizePackageAsync(
                package.Id,
                composite,
                targetUtc,
                targetEnd,
                expiresAt,
                allItems,
                plan,
                degradationReasons,
                ct);

            logger.LogInformation(
                "Scheduled package ready for {Target:u}: {Count} segment(s), {ItemCount} news item(s), announcement {AnnouncementId}",
                targetUtc,
                includedContributors.Count,
                allItems.Count,
                composite.Id);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
        catch (Exception ex) when (IsCancellationLikeFailure(ex, stoppingToken, ct))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                await MarkPackageStoppedAsync(
                    package.Id,
                    $"Production stopped during shutdown while {step}.",
                    allItems,
                    CancellationToken.None);
                logger.LogInformation(
                    "Top-of-hour package production stopped during shutdown for {Target:u}",
                    targetUtc);
                return await LoadPackageAsync(package.Id, CancellationToken.None);
            }

            metrics.GenerationFailed("news");
            await MarkPackageStoppedAsync(
                package.Id,
                IsAbortedIoFailure(ex)
                    ? $"Production stopped during {step}: {FailureDetail(ex)}"
                    : $"Production timed out or was cancelled during {step}: {FailureDetail(ex)}",
                allItems,
                CancellationToken.None);
            if (IsAbortedIoFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Top-of-hour package production aborted during {Step} for {Target:u}: {Message}",
                    step,
                    targetUtc,
                    ex.GetBaseException().Message);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "Top-of-hour package production timed out during {Step} for {Target:u}",
                    step,
                    targetUtc);
            }

            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.GenerationFailed("news");
            await MarkPackageStoppedAsync(package.Id, $"Production stopped during {step}: {FailureDetail(ex)}", allItems, ct);
            logger.LogWarning(
                ex,
                "Top-of-hour package production failed during {Step} for {Target:u}: {Message}",
                step,
                targetUtc,
                ex.GetBaseException().Message);
            return await LoadPackageAsync(package.Id, CancellationToken.None);
        }
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
    private async Task<ReusedSegment?> TryLoadSavedSegmentAsync(NewsPackageSegmentState saved, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var intro = await db.Announcements.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == saved.IntroAnnouncementId, ct);
        if (intro is null)
        {
            return null;
        }

        Announcement? body = null;
        if (saved.BodyAnnouncementId is { } bodyId)
        {
            body = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == bodyId, ct);
            if (body is null)
            {
                return null;
            }
        }

        Announcement? gapLine = null;
        if (saved.GapLineAnnouncementId is { } gapId)
        {
            gapLine = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == gapId, ct);
            if (gapLine is null)
            {
                return null;
            }
        }

        var host = await db.Moderators.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == saved.SegmentHostModeratorId, ct);
        if (host is null)
        {
            return null;
        }

        var items = saved.SelectedItemIds.Count == 0
            ? []
            : await db.NewsItems.AsNoTracking()
                .Include(item => item.Feed)
                .Where(item => saved.SelectedItemIds.Contains(item.Id))
                .ToListAsync(ct);

        return new ReusedSegment(host, intro, body, gapLine, items);
    }

    /// <summary>
    /// Expires the intro/body/gap-line audio of the given segments and marks the announcements as
    /// played so no playout path can air them independently of their package. The caller saves.
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

    private sealed record ReusedSegment(
        Moderator Host,
        Announcement Intro,
        Announcement? Body,
        Announcement? GapLine,
        IReadOnlyList<NewsItem> Items);

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

    internal static DateTimeOffset ResolveNextPackageTarget(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
        => ResolveNextPackagePlan(settings, localNow, contributors).TargetLocal;

    internal PackagePlan? ResolveNextPreparationPlan(StationSettings settings, DateTimeOffset localNow)
    {
        var plan = ResolveNextPackagePlan(settings, localNow, contributors);
        return plan.TargetLocal - localNow <= TimeSpan.FromMinutes(TopOfHourScheduler.DefaultPrepareAheadMinutes)
            ? plan
            : null;
    }

    internal static PackagePlan? ResolveNextPreparationPlan(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
    {
        var plan = ResolveNextPackagePlan(settings, localNow, contributors);
        return plan.TargetLocal - localNow <= TimeSpan.FromMinutes(TopOfHourScheduler.DefaultPrepareAheadMinutes)
            ? plan
            : null;
    }

    internal PackagePlan ResolveNextPackagePlan(StationSettings settings, DateTimeOffset localNow)
        => ResolveNextPackagePlan(settings, localNow, contributors);

    internal PackagePlan BuildPackagePlan(StationSettings settings, DateTimeOffset targetLocal)
        => BuildPackagePlan(settings, targetLocal, contributors);

    internal static PackagePlan ResolveNextPackagePlan(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
    {
        var enabled = contributors.Where(c => c.IsEnabled(settings)).ToList();
        if (enabled.Count == 0)
        {
            return BuildPackagePlan(settings, localNow, contributors);
        }

        // Pick the soonest cadence boundary across all enabled contributors.
        // At that target, each contributor checks whether its own cadence hits —
        // so a 60-min news + 30-min weather at :15 targets :30 (weather-only),
        // while at :45 it targets :00 (full block).
        DateTimeOffset soonest = DateTimeOffset.MaxValue;
        foreach (var contributor in enabled)
        {
            var target = NextContributorTarget(contributor, settings, localNow);
            if (target < soonest)
            {
                soonest = target;
            }
        }

        return BuildPackagePlan(settings, soonest, contributors);
    }

    internal static PackagePlan BuildPackagePlan(
        StationSettings settings,
        DateTimeOffset targetLocal,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
    {
        var included = contributors
            .Where(c => c.IsEnabled(settings) && c.IsIncludedAt(settings, targetLocal))
            .OrderBy(c => c.Order)
            .ToList();
        return new PackagePlan(targetLocal, included);
    }

    private static DateTimeOffset NextContributorTarget(
        ITopOfHourSegmentContributor contributor,
        StationSettings settings,
        DateTimeOffset localNow)
    {
        var cadence = contributor.CadenceMinutes(settings);
        var minuteOfDay = localNow.Hour * 60 + localNow.Minute;
        var nextMinute = minuteOfDay - minuteOfDay % cadence + cadence;
        return new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            0,
            0,
            0,
            localNow.Offset).AddMinutes(nextMinute);
    }

    private static int TargetDurationSeconds(StationSettings settings, PackagePlan plan)
        => plan.Segments.Count == 1 && plan.Segments[0].Key == "weather"
            ? 60
            : Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60);

    private static DateTimeOffset ToLocalTime(DateTime targetUtc, TimeSpan localOffset)
        => new DateTimeOffset(DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc), TimeSpan.Zero).ToOffset(localOffset);

    private static bool IsCancellationLikeFailure(
        Exception ex,
        CancellationToken stoppingToken,
        CancellationToken productionToken)
    {
        if (IsAbortedIoFailure(ex))
        {
            return true;
        }

        if (!stoppingToken.IsCancellationRequested && !productionToken.IsCancellationRequested)
        {
            return false;
        }

        var root = ex.GetBaseException();
        return ex is OperationCanceledException or TaskCanceledException or IOException or HttpRequestException
            || root is OperationCanceledException or TaskCanceledException or IOException or HttpRequestException;
    }

    private static bool IsAbortedIoFailure(Exception ex)
    {
        var root = ex.GetBaseException();
        return root is IOException
            && (root.Message.Contains("operation has been aborted", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("E/A-Vorgang wurde", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("Threadendes", StringComparison.OrdinalIgnoreCase)
                || root.Message.Contains("Anwendungsanforderung", StringComparison.OrdinalIgnoreCase));
    }

    private static string FailureDetail(Exception ex)
    {
        var root = ex.GetBaseException();
        var message = string.IsNullOrWhiteSpace(root.Message) ? ex.Message : root.Message;
        var detail = root.GetType() == ex.GetType()
            ? $"{root.GetType().Name}: {message}"
            : $"{ex.GetType().Name}/{root.GetType().Name}: {message}";
        return detail.Length <= 800 ? detail : detail[..800];
    }
}
