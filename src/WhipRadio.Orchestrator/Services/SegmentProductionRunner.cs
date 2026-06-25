using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Entities;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Shared helpers for turning a contributor's <see cref="SegmentDraftPlan"/> into voiced
/// announcements: the package pipeline uses <see cref="VoiceAsync"/> for its concurrent
/// fan-out, while <see cref="RunInlineAsync"/> backs the convenience
/// <see cref="ITopOfHourSegmentContributor.ProduceAsync"/> (sequential write-then-voice).
/// </summary>
public static class SegmentProductionRunner
{
    /// <summary>Voice one slot draft: an LLM draft via the factory's draft path, or a fixed
    /// direct announcement (handover fallback / gap line).</summary>
    public static async Task<Announcement> VoiceAsync(
        IServiceProvider services, SlotDraft slot, CancellationToken ct)
    {
        var factory = services.GetRequiredService<AnnouncementFactory>();
        if (slot.Draft is not null)
        {
            return await factory.ProduceFromDraftAsync(slot.Draft, ct);
        }

        var direct = slot.Direct
            ?? throw new InvalidOperationException("A slot draft must carry either a draft or direct text.");
        return await factory.ProduceDirectAsync(
            direct.Kind,
            direct.PartKind,
            direct.Priority,
            direct.Moderator,
            direct.Text,
            direct.Purpose,
            ct,
            title: direct.Title,
            expiresAtUtc: direct.ExpiresAtUtc,
            desiredDurationSeconds: direct.DesiredDurationSeconds,
            wordBudget: direct.WordBudget);
    }

    /// <summary>Plan, then write+voice each job in order, assembling a <see cref="SegmentResult"/>.</summary>
    public static async Task<SegmentResult> RunInlineAsync(
        ITopOfHourSegmentContributor contributor, SegmentProductionContext context, CancellationToken ct)
    {
        var plan = await contributor.PlanDraftsAsync(context, ct);

        Announcement? intro = null;
        Announcement? body = null;
        Announcement? gapLine = null;
        Announcement? outro = null;
        string? degradationReason = null;

        foreach (var job in plan.Jobs.OrderBy(job => job.Order))
        {
            var draft = await job.WriteAsync(context.ScopeServices, ct);
            var announcement = await VoiceAsync(context.ScopeServices, draft, ct);
            degradationReason ??= draft.DegradationReason;

            if (job.Slot == SegmentSlot.Handover)
            {
                intro = announcement;
            }
            else if (job.Slot == SegmentSlot.Outro)
            {
                outro = announcement;
            }
            else if (draft.IsGap)
            {
                gapLine = announcement;
            }
            else
            {
                body = announcement;
            }
        }

        return new SegmentResult(
            plan.Host,
            intro ?? throw new InvalidOperationException($"Segment '{plan.SegmentKey}' produced no handover."),
            body,
            gapLine,
            plan.Items,
            degradationReason,
            plan.SourceSummary,
            outro);
    }
}
