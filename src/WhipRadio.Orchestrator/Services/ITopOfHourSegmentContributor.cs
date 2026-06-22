using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Position of a contributor inside an ordered top-of-hour package. The first
/// contributor opens the block (time-aware intro); later contributors cut away
/// from the previous host.
/// </summary>
public enum SegmentPosition
{
    First,
    Middle,
    Last,
}

/// <summary>
/// Labeling for a single-segment package (when only one contributor is
/// included at a target). Multi-segment packages are always labeled as the
/// composite "Top of hour" block, regardless of degradation.
/// </summary>
public sealed record SegmentLabel(AnnouncementKind Kind, string Purpose, string Title);

/// <summary>
/// The host that voices a contributor's segments, plus the produced intro,
/// body, and an optional gap line. The intro always airs; the body may be null
/// when no data is available or production failed (in which case GapLine +
/// DegradationReason describe the gap).
/// </summary>
public sealed record SegmentResult(
    Moderator SegmentHost,
    Announcement Intro,
    Announcement? Body,
    Announcement? GapLine,
    IReadOnlyList<NewsItem> SelectedItems,
    string? DegradationReason,
    string SourceSummary);

/// <summary>
/// Everything a contributor needs to produce its segments for one package.
/// The orchestrator resolves the show context and a DI scope; contributors
/// resolve their own dependencies (AnnouncementFactory, data sources,
/// SpecialistHostCreationService, etc.) from <see cref="ScopeServices"/>.
/// <paramref name="ReportProgress"/> is the orchestrator's progress publisher
/// so contributors can surface "Resolving news specialist." etc. to the UI.
/// </summary>
public sealed record SegmentProductionContext(
    StationSettings Settings,
    DateTimeOffset TargetLocal,
    DateTime TargetUtc,
    DateTime ExpiresAtUtc,
    Moderator ShowModerator,
    SegmentPosition Position,
    Moderator? PreviousSegmentHost,
    IServiceProvider ScopeServices,
    Func<string, CancellationToken, Task> ReportProgress);

/// <summary>
/// A self-contained producer of one kind of top-of-hour segment (news,
/// weather, future traffic/Wikipedia/sports). The orchestrator collects all
/// registered contributors, picks the soonest cadence boundary across enabled
/// ones, and asks each whether it is included at that target. Each contributor
/// produces its own intro (LLM-written, position-aware) and body independently,
/// so one segment's failure never drops another segment's intro.
/// </summary>
public interface ITopOfHourSegmentContributor
{
    /// <summary>Stable key, e.g. "news", "weather".</summary>
    string Key { get; }

    /// <summary>
    /// Position within the package. Lower numbers air first (10=news,
    /// 20=weather, 30=traffic...). Contributors with the same Order air in
    /// registration order (avoid relying on this — pick distinct Orders).
    /// </summary>
    int Order { get; }

    /// <summary>Whether this segment is enabled in station settings.</summary>
    bool IsEnabled(StationSettings settings);

    /// <summary>Cadence in minutes (normalized by the contributor via the relevant scheduler).</summary>
    int CadenceMinutes(StationSettings settings);

    /// <summary>True when this contributor should air at the given target boundary.</summary>
    bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal);

    /// <summary>Labeling used when this contributor is the only one in the package.</summary>
    SegmentLabel Label { get; }

    /// <summary>Produce the intro + body (+ gap line) for one package.</summary>
    Task<SegmentResult> ProduceAsync(SegmentProductionContext context, CancellationToken ct);
}
