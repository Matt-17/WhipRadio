using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Closing segment for a top-of-hour block. After the specialists (news, weather) have aired,
/// the show host returns, thanks them by name, and hands back to the show — continuing the
/// current format or starting a new one. It produces no body and never airs on its own: it is
/// only included when at least one specialist is present in the same block. A concrete song
/// intro is deliberately NOT produced here — that is deferred to the normal song-intro flow,
/// which runs once the actual next track is known.
/// </summary>
public sealed class ShowReturnSegmentContributor : ITopOfHourSegmentContributor
{
    public string Key => "showreturn";
    public int Order => 30;
    public SegmentLabel Label => new(AnnouncementKind.StationId, "ShowReturn", "Back to the show");

    // Rides the single top-of-hour cadence (shared with news/weather) so it never invents its
    // own target — it simply closes whatever block the specialists already created.
    public bool IsEnabled(StationSettings settings) => settings.NewsEnabled || settings.WeatherEnabled;

    public int CadenceMinutes(StationSettings settings)
        => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes);

    public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
    {
        // Only close a block that actually has a specialist in it at this boundary.
        var cadence = CadenceMinutes(settings);
        var newsHere = settings.NewsEnabled && IsCadenceBoundary(targetLocal, cadence);
        var weatherHere = settings.WeatherEnabled && IsCadenceBoundary(targetLocal, cadence);
        return newsHere || weatherHere;
    }

    public Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct)
    {
        var showHost = context.ShowModerator;
        var facts = BuildReturnFacts(context);
        var jobs = new List<SegmentDraftJob>
        {
            new(SegmentSlot.Handover, 0, "show return",
                (sp, token) => WriteReturnAsync(sp, context, showHost, facts, token)),
        };

        return Task.FromResult(new SegmentDraftPlan(Key, showHost, [], "Back to the show", jobs));
    }

    private static async Task<SlotDraft> WriteReturnAsync(
        IServiceProvider sp,
        SegmentProductionContext context,
        Moderator showHost,
        string facts,
        CancellationToken ct)
    {
        var factory = sp.GetRequiredService<AnnouncementFactory>();
        await context.ReportProgress("Writing show return.", ct);
        try
        {
            var draft = await factory.WriteScriptDraftAsync(
                AnnouncementKind.StationId,
                showHost,
                relatedTrack: null,
                facts: facts,
                context.Settings.StationName,
                ct,
                lengthHint: "1-2 short, natural sentences.",
                alreadySpokenContext: null,
                localNowOverride: context.TargetLocal,
                priority: PromptPriority.High,
                purpose: "ShowReturn");
            return new SlotDraft(draft, null, IsGap: false, DegradationReason: null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            var specialists = context.PriorHosts.Count > 0
                ? string.Join(" and ", context.PriorHosts.Select(host => host.Name))
                : "the team";
            var direct = new DirectAnnouncementSpec(
                AnnouncementKind.StationId,
                TalkPartKind.StationId,
                TalkBreakPriority.Scheduled,
                showHost,
                $"Thanks, {specialists}. Let's get back to the music.",
                "ShowReturn",
                "Back to the show",
                context.ExpiresAtUtc,
                DesiredDurationSeconds: 6,
                WordBudget: 16);
            return new SlotDraft(null, direct, IsGap: false, DegradationReason: null);
        }
    }

    private static string BuildReturnFacts(SegmentProductionContext context)
    {
        var specialists = context.PriorHosts.Count > 0
            ? string.Join(", ", context.PriorHosts.Select(host => host.Name))
            : "the news team";
        var showNote = context.NewShowStartsAtTarget && !string.IsNullOrWhiteSpace(context.NextFormatName)
            ? $"A new show is starting now: \"{context.NextFormatName}\". Open it."
            : !string.IsNullOrWhiteSpace(context.CurrentFormatName)
                ? $"The current show \"{context.CurrentFormatName}\" continues. Pick it back up."
                : "Return to the show.";
        return $"Show host (speaking): {context.ShowModerator.Name}. "
            + $"Specialists to thank: {specialists}. {showNote} "
            + "Do not introduce any specific song.";
    }

    private static bool IsCadenceBoundary(DateTimeOffset localTime, int cadenceMinutes)
    {
        var minuteOfDay = localTime.Hour * 60 + localTime.Minute;
        return minuteOfDay % cadenceMinutes == 0;
    }
}
