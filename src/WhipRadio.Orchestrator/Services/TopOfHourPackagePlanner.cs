using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

/// <summary>The segments (news, weather, …) included in one package, the local
/// boundary it airs at, and whether it is the short top-of-hour block or a
/// scheduled long news format.</summary>
internal sealed record PackagePlan(
    DateTimeOffset TargetLocal,
    IReadOnlyList<ITopOfHourSegmentContributor> Segments,
    NewsPackageKind Kind = NewsPackageKind.TopOfHour);

/// <summary>
/// Pure cadence math for top-of-hour packages: which boundary comes next across
/// the enabled contributors, which segments are included at that boundary, and
/// whether production should already start (prepare-ahead window).
/// </summary>
internal static class TopOfHourPackagePlanner
{
    /// <summary>Long news blocks produce several times the audio of a short package —
    /// give them a longer production runway than the default prepare-ahead window.</summary>
    internal const int LongFormatPrepareAheadMinutes = 60;

    internal static DateTimeOffset ResolveNextPackageTarget(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
        => ResolveNextPackagePlan(settings, localNow, contributors).TargetLocal;

    internal static PackagePlan? ResolveNextPreparationPlan(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
        => ResolvePreparationPlans(settings, localNow, contributors).FirstOrDefault();

    /// <summary>
    /// All package targets whose preparation window is open, soonest first: the next
    /// cadence/air-time boundary (default window) plus — because it needs the longer
    /// runway — an upcoming long news block even when a nearer short target exists.
    /// </summary>
    internal static IReadOnlyList<PackagePlan> ResolvePreparationPlans(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
    {
        var all = contributors.ToList();
        var plans = new List<PackagePlan>();

        var next = ResolveNextPackagePlan(settings, localNow, all);
        var window = next.Kind == NewsPackageKind.LongFormat
            ? LongFormatPrepareAheadMinutes
            : TopOfHourScheduler.DefaultPrepareAheadMinutes;
        if (next.TargetLocal - localNow <= TimeSpan.FromMinutes(window))
        {
            plans.Add(next);
        }

        foreach (var contributor in all.Where(c => c.IsEnabled(settings)))
        {
            if (contributor.NextOwnTarget(settings, localNow) is not { } ownTarget
                || ownTarget - localNow > TimeSpan.FromMinutes(LongFormatPrepareAheadMinutes))
            {
                continue;
            }

            var plan = BuildPackagePlan(settings, ownTarget, all);
            if (plan.Kind == NewsPackageKind.LongFormat
                && plan.Segments.Count > 0
                && plans.All(existing => existing.TargetLocal != plan.TargetLocal))
            {
                plans.Add(plan);
            }
        }

        return plans.OrderBy(plan => plan.TargetLocal).ToList();
    }

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

        // A long news block owns its boundary outright: it replaces the short bulletin
        // (never both at one target) and turns the whole package into a LongFormat one.
        if (included.Any(c => c.Key == NewsLongFormatSegmentContributor.SegmentKey))
        {
            included.RemoveAll(c => c.Key == "news");
            return new PackagePlan(targetLocal, included, NewsPackageKind.LongFormat);
        }

        return new PackagePlan(targetLocal, included);
    }

    internal static DateTimeOffset ToLocalTime(DateTime targetUtc, TimeSpan localOffset)
        => new DateTimeOffset(DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc), TimeSpan.Zero).ToOffset(localOffset);

    private static DateTimeOffset NextContributorTarget(
        ITopOfHourSegmentContributor contributor,
        StationSettings settings,
        DateTimeOffset localNow)
    {
        if (contributor.NextOwnTarget(settings, localNow) is { } ownTarget)
        {
            return ownTarget;
        }

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
}
