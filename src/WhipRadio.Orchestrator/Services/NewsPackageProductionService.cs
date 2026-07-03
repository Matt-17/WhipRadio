using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class NewsPackageProductionService(
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
                var plan = BuildPackagePlan(settings, TopOfHourPackagePlanner.ToLocalTime(pending.TargetUtc, timeProvider.GetLocalNow().Offset));
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
        return await ProducePackageAsync(settings, targetUtc, plan: BuildPackagePlan(settings, TopOfHourPackagePlanner.ToLocalTime(targetUtc, timeProvider.GetLocalNow().Offset)), ct, budget.Token, packageId);
    }

    // Thin wrappers binding this service's contributor set to the pure planner.
    internal PackagePlan? ResolveNextPreparationPlan(StationSettings settings, DateTimeOffset localNow)
        => TopOfHourPackagePlanner.ResolveNextPreparationPlan(settings, localNow, contributors);

    internal PackagePlan ResolveNextPackagePlan(StationSettings settings, DateTimeOffset localNow)
        => TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, contributors);

    internal PackagePlan BuildPackagePlan(StationSettings settings, DateTimeOffset targetLocal)
        => TopOfHourPackagePlanner.BuildPackagePlan(settings, targetLocal, contributors);

    private static int TargetDurationSeconds(StationSettings settings, PackagePlan plan)
        => plan.Segments.Count == 1 && plan.Segments[0].Key == "weather"
            ? 60
            : Math.Clamp(settings.NewsPackageMaxDurationSeconds, 60, 30 * 60);

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
