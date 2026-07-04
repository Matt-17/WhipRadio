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

/// <summary>Which part of a segment a draft job produces: the spoken handover into the
/// segment, the segment body (bulletin/forecast, which may degrade to a gap line), or a
/// closing outro that airs after the body (e.g. the news host returning after the weather).</summary>
public enum SegmentSlot
{
    Handover,
    Body,
    Outro,
}

/// <summary>A ready-to-voice direct announcement (no LLM): used for handover fallbacks and
/// gap lines. Mirrors the parameters of <see cref="AnnouncementFactory.ProduceDirectAsync"/>.</summary>
public sealed record DirectAnnouncementSpec(
    AnnouncementKind Kind,
    TalkPartKind PartKind,
    TalkBreakPriority Priority,
    Moderator Moderator,
    string Text,
    string Purpose,
    string Title,
    DateTime? ExpiresAtUtc,
    int? DesiredDurationSeconds,
    int? WordBudget);

/// <summary>
/// The text result of one draft job, ready for voicing. Exactly one of <see cref="Draft"/>
/// (LLM-written, voice via <c>ProduceFromDraftAsync</c>) or <see cref="Direct"/> (fixed text,
/// voice via <c>ProduceDirectAsync</c>) is set. <see cref="IsGap"/> marks a body slot that
/// fell back to a short gap line.
/// </summary>
public sealed record SlotDraft(
    AnnouncementFactory.AnnouncementScriptDraft? Draft,
    DirectAnnouncementSpec? Direct,
    bool IsGap,
    string? DegradationReason);

/// <summary>
/// One independent script-writing unit within a segment. <see cref="WriteAsync"/> performs
/// only the (GPU) text work and resolves its services from the supplied scope, so the
/// orchestrator can run every job's write concurrently and voice each result as it lands.
/// </summary>
public sealed record SegmentDraftJob(
    SegmentSlot Slot,
    int Order,
    string ProgressLabel,
    Func<IServiceProvider, CancellationToken, Task<SlotDraft>> WriteAsync);

/// <summary>
/// The outcome of a contributor's (sequential, cheap) preparation: the resolved host, the
/// news items + source summary for the package, and the independent draft jobs to run.
/// </summary>
public sealed record SegmentDraftPlan(
    string SegmentKey,
    Moderator Host,
    IReadOnlyList<NewsItem> Items,
    string SourceSummary,
    IReadOnlyList<SegmentDraftJob> Jobs);

/// <summary>
/// Labeling for a single-segment package (when only one contributor is
/// included at a target). Multi-segment packages are always labeled as the
/// composite "Top of hour" block, regardless of degradation.
/// </summary>
public sealed record SegmentLabel(AnnouncementKind Kind, string Purpose, string Title);

/// <summary>
/// The host that voices a contributor's segments, plus the produced intro,
/// body, an optional gap line, and an optional outro that airs after the body
/// (e.g. the news host returning after the weather). The intro always airs; the
/// body may be null when no data is available or production failed (in which case
/// GapLine + DegradationReason describe the gap).
/// </summary>
public sealed record SegmentResult(
    Moderator SegmentHost,
    Announcement Intro,
    Announcement? Body,
    Announcement? GapLine,
    IReadOnlyList<NewsItem> SelectedItems,
    string? DegradationReason,
    string SourceSummary,
    Announcement? Outro = null);

/// <summary>
/// Everything a contributor needs to produce its segments for one package.
/// The orchestrator resolves the show context and a DI scope; contributors
/// resolve their own dependencies (AnnouncementFactory, data sources,
/// SpecialistHostCreationService, etc.) from <see cref="ScopeServices"/>.
/// <paramref name="ReportProgress"/> is the orchestrator's progress publisher
/// so contributors can surface "Resolving news specialist." etc. to the UI.
/// <para>
/// <see cref="PreviousSegmentHosts"/> is the ordered list of every specialist that
/// already spoke earlier in this block (the news host before weather, etc.). The news
/// host brackets the weather, and the closing show-return thanks every prior host, so a
/// contributor only ever needs to name hosts that came before it — no forward resolution.
/// <see cref="CurrentFormatName"/>/<see cref="NextFormatName"/>/<see cref="NewShowStartsAtTarget"/>
/// let the show-return reflect whether the show is continuing or a new one is starting.
/// </para>
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
    Func<string, CancellationToken, Task> ReportProgress,
    IReadOnlyList<Moderator>? PreviousSegmentHosts = null,
    string? CurrentFormatName = null,
    string? NextFormatName = null,
    bool NewShowStartsAtTarget = false)
{
    /// <summary>Every specialist that already spoke in this block, in air order (never null).</summary>
    public IReadOnlyList<Moderator> PriorHosts => PreviousSegmentHosts ?? Array.Empty<Moderator>();
}

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

    /// <summary>
    /// Contributors whose air times are not cadence-based (the long news format airs at
    /// operator-configured wall-clock times) return their next target here; the planner
    /// then uses it instead of the cadence boundary math. Null = cadence-based (default).
    /// </summary>
    DateTimeOffset? NextOwnTarget(StationSettings settings, DateTimeOffset localNow) => null;

    /// <summary>True when this contributor should air at the given target boundary.</summary>
    bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal);

    /// <summary>Labeling used when this contributor is the only one in the package.</summary>
    SegmentLabel Label { get; }

    /// <summary>
    /// Prepare this segment (resolve specialist/host, gather data) and return the independent
    /// script-writing jobs. No TTS happens here — the orchestrator voices each draft as it
    /// completes, ordered against everyone else on the shared GPU. To produce a whole segment
    /// inline (write then voice in order), use <see cref="SegmentProductionRunner.RunInlineAsync"/>.
    /// </summary>
    Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct);
}
