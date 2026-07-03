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
            // Every writer + voice job in this package inherits one ramping priority: news
            // climbs from Low to Highest as its target air time approaches, re-evaluated each
            // time the GPU scheduler picks the next job.
            using var newsPriority = GpuPriorityContext.Push(
                () => NewsAirtimeRamp.Priority(targetUtc, timeProvider.GetUtcNow().UtcDateTime));

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

            // Whether the current show ends at the top of the hour (so the show host returns
            // by STARTING a new format) or runs through it (CONTINUING). Best-effort from the
            // schedule; the show-return prompt phrases the handover accordingly.
            var minutesUntilTarget = (targetLocal - localNow).TotalMinutes;
            var newShowStartsAtTarget = context.RemainingSlotMinutes is { } remainingSlotMinutes
                && remainingSlotMinutes <= minutesUntilTarget + 1.0
                && !string.IsNullOrWhiteSpace(context.NextFormatName);

            using var scope = scopeFactory.CreateScope();
            var renderer = scope.ServiceProvider.GetRequiredService<SegmentRenderer>();

            // --- Phase 1: sequential, cheap prep. Reuse finished segments; otherwise resolve
            // the host/items and capture the (text-only) draft jobs to run. No GPU work yet.
            var prepared = new List<PreparedSegment>();
            Moderator? previousSegmentHost = null;
            // Every specialist that already spoke this block, in air order: the news host
            // brackets the weather and the show-return thanks them all, so a contributor only
            // ever names hosts ahead of it in this list — no forward host resolution needed.
            var priorHosts = new List<Moderator>();
            for (var i = 0; i < includedContributors.Count; i++)
            {
                var contributor = includedContributors[i];
                var position = i == 0
                    ? SegmentPosition.First
                    : i == includedContributors.Count - 1
                        ? SegmentPosition.Last
                        : SegmentPosition.Middle;

                var saved = producedSegments.FirstOrDefault(s => s.Key == contributor.Key && s.Done);
                if (saved is not null)
                {
                    var reused = await TryLoadSavedSegmentAsync(saved, ct);
                    if (reused is not null)
                    {
                        prepared.Add(PreparedSegment.FromReuse(reused, saved.DegradationReason));
                        previousSegmentHost = reused.Host;
                        priorHosts.Add(reused.Host);
                        continue;
                    }

                    // The saved audio is gone — forget it and re-produce.
                    producedSegments.RemoveAll(s => s.Key == contributor.Key);
                }

                step = $"preparing {contributor.Key} segment";
                await UpdatePackageProductionStateAsync(package.Id, $"Preparing {contributor.Key} segment.", ct);

                var segmentContext = new SegmentProductionContext(
                    settings,
                    targetLocal,
                    targetUtc,
                    expiresAt,
                    context.Moderator,
                    position,
                    previousSegmentHost,
                    scope.ServiceProvider,
                    (state, token) => UpdatePackageProductionStateAsync(package.Id, state, token),
                    PreviousSegmentHosts: priorHosts.ToList(),
                    CurrentFormatName: context.Format?.Name,
                    NextFormatName: context.NextFormatName,
                    NewShowStartsAtTarget: newShowStartsAtTarget);

                try
                {
                    var draftPlan = await contributor.PlanDraftsAsync(segmentContext, ct);
                    prepared.Add(PreparedSegment.FromPlan(draftPlan));
                    previousSegmentHost = draftPlan.Host;
                    priorHosts.Add(draftPlan.Host);
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
                        "Top-of-hour {Key} contributor prep failed: {Message}",
                        contributor.Key,
                        ex.GetBaseException().Message);
                    degradationReasons.Add($"{contributor.Key} segment failed: {FailureDetail(ex)}");
                }
            }

            // --- Phase 2: queue every script write at once and voice each draft as it lands.
            // The GPU scheduler orders writes vs. voices by priority -> affinity -> FIFO, so a
            // high-priority handover recording may finish before a later script is even written.
            step = "writing and recording segments";
            var plannedPlans = prepared.Where(p => p.Plan is not null).Select(p => p.Plan!).ToList();
            var jobCount = plannedPlans.Sum(draftPlan => draftPlan.Jobs.Count);
            // load context (1) + one step per write and per voice + schedule/render/finalize (3).
            var stepTotal = 1 + (jobCount * 2) + 3;
            var stepCounter = new StepCounter(startAt: 1);
            using var dbGate = new SemaphoreSlim(1, 1);

            var runResults = await Task.WhenAll(plannedPlans.Select(draftPlan =>
                RunPlannedSegmentAsync(draftPlan, package.Id, stepTotal, stepCounter, dbGate, producedSegments, ct)));
            var resultsByKey = runResults.ToDictionary(result => result.SegmentKey);

            // Reassemble in contributor order (independent of voice completion order).
            foreach (var prep in prepared)
            {
                if (prep.Reused is { } reused)
                {
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
                    if (reused.Outro is not null)
                    {
                        producedAnnouncements.Add(reused.Outro);
                    }
                    allItems.AddRange(reused.Items);
                    if (prep.SavedDegradationReason is not null)
                    {
                        degradationReasons.Add(prep.SavedDegradationReason);
                    }
                    continue;
                }

                if (prep.Plan is null || !resultsByKey.TryGetValue(prep.Plan.SegmentKey, out var result))
                {
                    continue;
                }

                firstSegmentHost ??= result.Host;
                fallbackModerator ??= result.Host;
                if (result.Intro is not null)
                {
                    producedAnnouncements.Add(result.Intro);
                }
                if (result.Body is not null)
                {
                    producedAnnouncements.Add(result.Body);
                }
                if (result.GapLine is not null)
                {
                    producedAnnouncements.Add(result.GapLine);
                }
                if (result.Outro is not null)
                {
                    producedAnnouncements.Add(result.Outro);
                }
                allItems.AddRange(result.Items);
                degradationReasons.AddRange(result.DegradationReasons);
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
            await UpdateStepAsync(package.Id, stepTotal - 2, stepTotal, "Scheduling package audio.", ct);
            await MarkScheduledAsync(producedAnnouncements.Select(a => a.Id).ToList(), targetUtc, targetEnd, expiresAt, ct);
            step = "rendering package audio";
            await UpdateStepAsync(package.Id, stepTotal - 1, stepTotal, "Rendering package audio.", ct);
            var fallback = fallbackModerator ?? context.Moderator;
            var composite = producedAnnouncements.Count == 1
                ? producedAnnouncements[0]
                : await renderer.RenderAsync(producedAnnouncements, fallback, ct);
            step = "finalizing package";
            await UpdateStepAsync(package.Id, stepTotal, stepTotal, "Finalizing package.", ct);
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

        Announcement? outro = null;
        if (saved.OutroAnnouncementId is { } outroId)
        {
            outro = await db.Announcements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == outroId, ct);
            if (outro is null)
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

        return new ReusedSegment(host, intro, body, gapLine, items, outro);
    }

    /// <summary>
    /// Expires the intro/body/gap-line audio of the given segments and marks the announcements as
    /// played so no playout path can air them independently of their package. The caller saves.
    /// </summary>

    private async Task<SegmentRunResult> RunPlannedSegmentAsync(
        SegmentDraftPlan plan,
        Guid packageId,
        int stepTotal,
        StepCounter stepCounter,
        SemaphoreSlim dbGate,
        List<NewsPackageSegmentState> producedSegments,
        CancellationToken ct)
    {
        var slots = await Task.WhenAll(
            plan.Jobs.Select(job => RunSlotAsync(plan, job, packageId, stepTotal, stepCounter, dbGate, ct)));

        Announcement? intro = null;
        Announcement? body = null;
        Announcement? gapLine = null;
        Announcement? outro = null;
        var degradations = new List<string>();
        foreach (var slot in slots)
        {
            if (slot.DegradationReason is not null)
            {
                degradations.Add(slot.DegradationReason);
            }

            if (slot.Slot == SegmentSlot.Handover)
            {
                intro = slot.Announcement;
            }
            else if (slot.Slot == SegmentSlot.Outro)
            {
                outro = slot.Announcement;
            }
            else if (slot.IsGap)
            {
                gapLine = slot.Announcement;
            }
            else
            {
                body = slot.Announcement;
            }
        }

        // Persist for resume once the whole segment is voiced (only when the handover aired).
        if (intro is not null)
        {
            await dbGate.WaitAsync(ct);
            try
            {
                producedSegments.RemoveAll(s => s.Key == plan.SegmentKey);
                producedSegments.Add(new NewsPackageSegmentState
                {
                    Key = plan.SegmentKey,
                    Done = true,
                    IntroAnnouncementId = intro.Id,
                    BodyAnnouncementId = body?.Id,
                    GapLineAnnouncementId = gapLine?.Id,
                    OutroAnnouncementId = outro?.Id,
                    SegmentHostModeratorId = plan.Host.Id,
                    DegradationReason = degradations.FirstOrDefault(),
                    SourceSummary = plan.SourceSummary,
                    SelectedItemIds = plan.Items.Select(item => item.Id).ToList(),
                });
                await PersistSegmentsAsync(packageId, producedSegments, ct);
            }
            finally
            {
                dbGate.Release();
            }
        }

        return new SegmentRunResult(plan.SegmentKey, plan.Host, intro, body, gapLine, plan.Items, degradations, outro);
    }

    /// <summary>Write one slot's script (its own DI scope), then voice it. The production state is
    /// announced before each write and each recording starts, so it reflects the work currently in
    /// flight rather than the step that just finished. Each write and each recording advances the
    /// production step counter.</summary>
    private async Task<SlotRunResult> RunSlotAsync(
        SegmentDraftPlan plan,
        SegmentDraftJob job,
        Guid packageId,
        int stepTotal,
        StepCounter stepCounter,
        SemaphoreSlim dbGate,
        CancellationToken ct)
    {
        using var slotScope = scopeFactory.CreateScope();
        var services = slotScope.ServiceProvider;

        await BumpStepAsync(packageId, stepCounter, stepTotal, dbGate, $"Writing {job.ProgressLabel}.", ct);

        SlotDraft draft;
        try
        {
            draft = await job.WriteAsync(services, ct);
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
                "Top-of-hour {Segment} {Slot} writing failed: {Message}",
                plan.SegmentKey, job.ProgressLabel, ex.GetBaseException().Message);
            // Writing failed — still account for the recording step that will not run.
            await BumpStepAsync(packageId, stepCounter, stepTotal, dbGate, $"Recording {job.ProgressLabel}.", ct);
            return new SlotRunResult(
                job.Slot, null, job.Slot == SegmentSlot.Body, $"{job.ProgressLabel} writing failed: {FailureDetail(ex)}");
        }

        await BumpStepAsync(packageId, stepCounter, stepTotal, dbGate, $"Recording {job.ProgressLabel}.", ct);

        Announcement? announcement = null;
        var degradation = draft.DegradationReason;
        try
        {
            announcement = await SegmentProductionRunner.VoiceAsync(services, draft, ct);
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
                "Top-of-hour {Segment} {Slot} recording failed: {Message}",
                plan.SegmentKey, job.ProgressLabel, ex.GetBaseException().Message);
            degradation ??= $"{job.ProgressLabel} recording failed: {FailureDetail(ex)}";
        }

        return new SlotRunResult(job.Slot, announcement, draft.IsGap, degradation);
    }

    private async Task BumpStepAsync(
        Guid packageId, StepCounter counter, int total, SemaphoreSlim dbGate, string state, CancellationToken ct)
    {
        var index = counter.Next();
        await dbGate.WaitAsync(ct);
        try
        {
            await UpdateStepAsync(packageId, index, total, state, ct);
        }
        finally
        {
            dbGate.Release();
        }
    }

    private sealed record PreparedSegment(
        SegmentDraftPlan? Plan, ReusedSegment? Reused, string? SavedDegradationReason)
    {
        public static PreparedSegment FromPlan(SegmentDraftPlan plan) => new(plan, null, null);

        public static PreparedSegment FromReuse(ReusedSegment reused, string? degradation)
            => new(null, reused, degradation);
    }

    private sealed record SegmentRunResult(
        string SegmentKey,
        Moderator Host,
        Announcement? Intro,
        Announcement? Body,
        Announcement? GapLine,
        IReadOnlyList<NewsItem> Items,
        IReadOnlyList<string> DegradationReasons,
        Announcement? Outro = null);

    private sealed record SlotRunResult(
        SegmentSlot Slot, Announcement? Announcement, bool IsGap, string? DegradationReason);

    private sealed class StepCounter(int startAt)
    {
        private int _value = startAt;

        public int Next() => Interlocked.Increment(ref _value);
    }

    private sealed record ReusedSegment(
        Moderator Host,
        Announcement Intro,
        Announcement? Body,
        Announcement? GapLine,
        IReadOnlyList<NewsItem> Items,
        Announcement? Outro = null);
}
