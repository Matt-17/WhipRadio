using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public class MixerOverviewService(
    IDbContextFactory<RadioDbContext> dbFactory,
    MixerDiagnostics diagnostics)
{
    public async Task<MixerOverviewDto> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        var settings = new MixerSettingsDto(
            s.MixerEnabled, s.TargetLufs, s.MaxMakeupGainDb, s.DuckLevelDb, s.DuckRampMs,
            s.DefaultCrossfadeSeconds, s.BeatAlignBpmTolerancePct,
            s.HardCutGapAfterTalkMsMin, s.HardCutGapAfterTalkMsMax,
            s.HardCutGapSongMsMin, s.HardCutGapSongMsMax,
            s.PostHitSafetyMs, s.StrategyWeightsJson, s.AnalysisRequired);

        var analyzedTracks = await db.MediaAnalyses
            .CountAsync(a => a.ItemType == PlayoutItemType.Track && a.AnalyzerVersion > 0, ct);
        var totalTracks = await db.Tracks.CountAsync(t => !t.IsRetired, ct);
        var analyzedAnnouncements = await db.MediaAnalyses
            .CountAsync(a => a.ItemType == PlayoutItemType.Announcement && a.AnalyzerVersion > 0, ct);

        var byStrategy = await db.TransitionLog.AsNoTracking()
            .GroupBy(e => e.Strategy)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var totalClips = await db.TransitionLog.AsNoTracking().SumAsync(e => (int?)e.ClipCount, ct) ?? 0;

        var recent = await db.TransitionLog.AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        var transitionIds = recent
            .Select(r => r.OutgoingId)
            .Concat(recent.Select(r => r.IncomingId))
            .ToList();
        var trackTitles = await db.Tracks.AsNoTracking()
            .Where(t => transitionIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var announcementKinds = await db.Announcements.AsNoTracking()
            .Where(a => transitionIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Kind.ToString(), ct);

        string Title(PlayoutItemType type, Guid id) => type == PlayoutItemType.Track
            ? trackTitles.GetValueOrDefault(id, "track")
            : $"{announcementKinds.GetValueOrDefault(id, "talk")} (talk)";

        var status = new MixerStatusDto(
            analyzedTracks, totalTracks, analyzedAnnouncements, byStrategy, totalClips,
            recent.Select(e => new TransitionLogEntryDto(
                e.OccurredAt, e.Strategy,
                Title(e.OutgoingType, e.OutgoingId), Title(e.IncomingType, e.IncomingId),
                e.OverlapSeconds, e.GapMs, e.ClipCount, Trace(e.ParametersJson))).ToList());

        var live = diagnostics.Snapshot();
        return new MixerOverviewDto(settings, status, new MixerLiveDto(
            live.Active, live.EngagedAtUtc, live.MasterSeconds, live.ActiveItems,
            live.LastDecision, live.LastDecisionAtUtc, live.Transitions));
    }

    private static string? Trace(string parametersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            return doc.RootElement.TryGetProperty("reasonTrace", out var trace) ? trace.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
