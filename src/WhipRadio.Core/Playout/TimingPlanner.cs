using System.Globalization;

namespace WhipRadio.Core.Playout;

/// <summary>A playable station-ID jingle offered to the planner as gap fill.</summary>
public sealed record JingleCandidate(
    Guid Id,
    string FilePath,
    string Label,
    double DurationSeconds,
    DateTime? LastUsedAtUtc);

/// <summary>Everything the planner needs to know about the approach to the next package.</summary>
public sealed record TimingPlannerInput(
    DateTime UtcNow,
    DateTime? NextPackageTargetUtc,
    double QueuedSecondsAhead,
    double MaxTrackDurationSeconds,
    double MinTrackDurationSeconds,
    IReadOnlyList<JingleCandidate> Jingles,
    int IntroGraceSeconds,
    int CurrentItemFinishGraceSeconds = TopOfHourScheduler.DefaultCurrentItemFinishGraceSeconds);

public enum TimingAction
{
    /// <summary>No package within reach — enqueue normally.</summary>
    NoConstraint,

    /// <summary>Enqueue a track, but prefer one no longer than <see cref="TimingDecision.MaxTrackDurationSeconds"/>.</summary>
    EnqueueTrackCapped,

    /// <summary>Bridge the remaining gap with the chosen station-ID jingle.</summary>
    EnqueueJingleFill,

    /// <summary>Enqueue nothing — let the dispatcher land the package (fade if needed).</summary>
    WaitForPackage,
}

/// <summary>One enqueue-time decision; Reason is always populated so fallbacks are logged.</summary>
public sealed record TimingDecision(
    TimingAction Action,
    double? MaxTrackDurationSeconds = null,
    JingleCandidate? Jingle = null,
    string Reason = "");

/// <summary>
/// Pure enqueue-time strategy for landing top-of-hour/long-format packages at their
/// target (brief §4): (1) pick a track that fits the remaining time, (2) bridge small
/// gaps with a station-ID jingle, (3) inside the intro grace, stop enqueueing and let
/// the dispatcher claim the boundary, (4) the mixer's timed-interrupt fade stays the
/// last resort. Music is never time-stretched — timing is solved with selection,
/// fills, and fades only.
/// </summary>
public static class TimingPlanner
{
    public static TimingDecision Decide(TimingPlannerInput input)
        => Decide(input, allowTrack: true);

    /// <summary>
    /// Re-decides after a capped pick came back longer than the cap (the selector's
    /// duration filter is soft): the track option is off the table, so the gap is
    /// bridged with a jingle or handed to the dispatcher.
    /// </summary>
    public static TimingDecision DecideAfterUnfitTrackPick(TimingPlannerInput input)
        => Decide(input, allowTrack: false);

    public static double RemainingSecondsBeforeTarget(TimingPlannerInput input)
        => input.NextPackageTargetUtc is not { } target
            ? double.PositiveInfinity
            : (target - input.UtcNow).TotalSeconds - Math.Max(0, input.QueuedSecondsAhead);

    private static TimingDecision Decide(TimingPlannerInput input, bool allowTrack)
    {
        var remaining = RemainingSecondsBeforeTarget(input);
        var finishGrace = TopOfHourScheduler.NormalizeCurrentItemFinishGraceSeconds(
            input.CurrentItemFinishGraceSeconds);
        var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(input.IntroGraceSeconds);
        var maxTrack = Math.Max(1, input.MaxTrackDurationSeconds);
        var minTrack = Math.Clamp(input.MinTrackDurationSeconds, 1, maxTrack);

        if (input.NextPackageTargetUtc is null)
        {
            return new TimingDecision(TimingAction.NoConstraint, Reason: "no scheduled package within reach");
        }

        if (remaining > maxTrack + finishGrace)
        {
            return new TimingDecision(
                TimingAction.NoConstraint,
                Reason: Invariant($"{remaining:F0}s before target — any track fits"));
        }

        if (allowTrack && remaining >= minTrack)
        {
            // A song may run up to the finish grace past the target; the mixer's
            // timed interrupt fades whatever is still playing at the boundary.
            var cap = Math.Max(remaining + finishGrace, minTrack);
            return new TimingDecision(
                TimingAction.EnqueueTrackCapped,
                MaxTrackDurationSeconds: cap,
                Reason: Invariant($"{remaining:F0}s before target — capping track pick at {cap:F0}s"));
        }

        if (remaining <= introGrace)
        {
            return new TimingDecision(
                TimingAction.WaitForPackage,
                Reason: Invariant(
                    $"{remaining:F0}s before target is inside the {introGrace}s intro grace — holding for the package"));
        }

        var jingle = PickBestFitJingle(remaining, input.Jingles, introGrace);
        if (jingle is not null)
        {
            return new TimingDecision(
                TimingAction.EnqueueJingleFill,
                Jingle: jingle,
                Reason: Invariant(
                    $"{remaining:F0}s gap before target — bridging with station ID \"{jingle.Label}\" ({jingle.DurationSeconds:F0}s)"));
        }

        return new TimingDecision(
            TimingAction.WaitForPackage,
            Reason: Invariant(
                $"no jingle fits the {remaining:F0}s gap — falling back to hold + fade landing"));
    }

    /// <summary>
    /// Best fit = the longest jingle that still ends by target + intro grace;
    /// ties go to the least recently used one so station IDs rotate.
    /// </summary>
    public static JingleCandidate? PickBestFitJingle(
        double remainingSeconds, IReadOnlyList<JingleCandidate> jingles, int introGraceSeconds)
    {
        var budget = remainingSeconds + TopOfHourScheduler.NormalizeIntroGraceSeconds(introGraceSeconds);
        return jingles
            .Where(jingle => jingle.DurationSeconds > 0 && jingle.DurationSeconds <= budget)
            .OrderByDescending(jingle => jingle.DurationSeconds)
            .ThenBy(jingle => jingle.LastUsedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private static string Invariant(FormattableString text)
        => text.ToString(CultureInfo.InvariantCulture);
}
