using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

/// <summary>The segments (news, weather, …) included in one top-of-hour package
/// and the local boundary it airs at.</summary>
internal sealed record PackagePlan(
    DateTimeOffset TargetLocal,
    IReadOnlyList<ITopOfHourSegmentContributor> Segments);

/// <summary>
/// Pure cadence math for top-of-hour packages: which boundary comes next across
/// the enabled contributors, which segments are included at that boundary, and
/// whether production should already start (prepare-ahead window).
/// </summary>
internal static class TopOfHourPackagePlanner
{
    internal static DateTimeOffset ResolveNextPackageTarget(
        StationSettings settings,
        DateTimeOffset localNow,
        IEnumerable<ITopOfHourSegmentContributor> contributors)
        => ResolveNextPackagePlan(settings, localNow, contributors).TargetLocal;

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

    internal static DateTimeOffset ToLocalTime(DateTime targetUtc, TimeSpan localOffset)
        => new DateTimeOffset(DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc), TimeSpan.Zero).ToOffset(localOffset);

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
}
