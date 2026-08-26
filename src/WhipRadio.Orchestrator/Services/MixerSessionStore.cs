using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>A scheduled package/episode that must hold track starts around its boundary.</summary>
public sealed record TopOfHourGuard(
    DateTime TargetUtc,
    int IntroGraceSeconds,
    int LateWindowSeconds,
    double FadeOutSeconds,
    NewsPackageStatus Status);

/// <summary>
/// The mixer session's database surface: item analysis, hot-reloadable settings,
/// the top-of-hour guard query, and the transition log. Keeps the engine itself
/// free of EF so its clock loop stays pure orchestration.
/// </summary>
public sealed class MixerSessionStore(
    IDbContextFactory<RadioDbContext> dbFactory,
    IMixerUpdatePublisher mixerUpdates,
    IStationMetrics metrics,
    ILogger<MixerSessionStore> logger)
{
    public async Task<ItemInfo> BuildItemInfoAsync(PlayoutItem item, CancellationToken ct)
    {
        MediaAnalysis? analysis = null;
        double? talkativeness = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            analysis = await db.MediaAnalyses.AsNoTracking()
                .FirstOrDefaultAsync(a => a.ItemType == item.ItemType && a.ItemId == item.ItemId, ct);
            if (analysis is { AnalyzerVersion: 0 })
            {
                analysis = null; // stub row from a failed analysis — planner degrades
            }

            // The host has a vote on talk-over transitions.
            if (item.ItemType == PlayoutItemType.Announcement && item.ModeratorId is { } moderatorId)
            {
                talkativeness = await db.Moderators.AsNoTracking()
                    .Where(m => m.Id == moderatorId)
                    .Select(m => (double?)m.Talkativeness)
                    .FirstOrDefaultAsync(ct);
            }
        }
        catch
        {
            // analysis/host context is optional by design
        }

        // Announcements have a reliable duration from TTS/rendering (item.DurationSeconds).
        // The analysis sidecar can report wrong durations for speech files (e.g. stopping
        // at the first silence gap), which causes the mixer to cut the announcement short.
        // Only use the analysis duration for tracks, where it measures the real audio.
        var duration = item.ItemType == PlayoutItemType.Announcement
            ? item.DurationSeconds
            : analysis is { DurationSeconds: > 0 } ? analysis.DurationSeconds : item.DurationSeconds;

        if (duration <= 0)
        {
            logger.LogWarning(
                "Mixer: \"{Title}\" ({ItemType} {ItemId}) has DurationSeconds={Duration:F3} — "
                + "zero-length source will be skipped. File path: \"{FilePath}\"",
                item.Title, item.ItemType, item.ItemId, duration, item.FilePath);
        }

        return new ItemInfo(item.ItemType, analysis, duration, talkativeness);
    }

    public async Task<MixerSettings> LoadSettingsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            return new MixerSettings(
                s.TargetLufs, s.MaxMakeupGainDb, s.DuckLevelDb, s.DuckRampMs,
                s.DefaultCrossfadeSeconds, s.BeatAlignBpmTolerancePct,
                s.HardCutGapAfterTalkMsMin, s.HardCutGapAfterTalkMsMax,
                s.HardCutGapSongMsMin, s.HardCutGapSongMsMax,
                s.PostHitSafetyMs, s.StrategyWeightsJson);
        }
        catch
        {
            return new MixerSettings();
        }
    }

    public async Task<TopOfHourGuard?> GetTopOfHourGuardAsync(DateTime utcNow, TimeSpan horizon, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

            var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
            var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
            var fadeOut = TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds);
            var minTarget = utcNow.AddSeconds(-lateWindow);
            var maxTarget = utcNow
                .Add(horizon < TimeSpan.Zero ? TimeSpan.Zero : horizon)
                .AddSeconds(introGrace);
            // The hold ONLY engages for a package that is actually ready to air
            // (Ready/Queued). A Pending/Retrying package is still being produced and
            // has no audio yet — holding for it would stop the song and stream silence
            // until production finishes. The rule is: keep playing music while the news
            // is pending; the dispatcher + timed interrupt cut it in the instant it
            // becomes Ready (immediately, even past the top of the hour).
            NewsPackage? package = null;
            if (settings.NewsEnabled || settings.WeatherEnabled)
            {
                package = await db.NewsPackages.AsNoTracking()
                    .Where(package => package.TargetUtc >= minTarget
                        && package.TargetUtc <= maxTarget
                        && (package.Status == NewsPackageStatus.Ready
                            || package.Status == NewsPackageStatus.Queued))
                    .OrderBy(package => package.TargetUtc)
                    .FirstOrDefaultAsync(ct);
            }

            // Scheduled podcast episodes land through the same timed interrupt and
            // need the same hold. Produced ≈ Ready, Queued ≈ Queued.
            var episode = await db.ConversationSegments.AsNoTracking()
                .Where(segment => segment.TargetUtc != null
                    && segment.TargetUtc >= minTarget
                    && segment.TargetUtc <= maxTarget
                    && (segment.Status == ConversationStatus.Produced
                        || segment.Status == ConversationStatus.Queued))
                .OrderBy(segment => segment.TargetUtc)
                .Select(segment => new { TargetUtc = segment.TargetUtc!.Value, segment.Status })
                .FirstOrDefaultAsync(ct);

            if (package is null && episode is null)
            {
                return null;
            }

            if (episode is not null && (package is null || episode.TargetUtc < package.TargetUtc))
            {
                var episodeStatus = episode.Status == ConversationStatus.Queued
                    ? NewsPackageStatus.Queued
                    : NewsPackageStatus.Ready;
                return new TopOfHourGuard(episode.TargetUtc, introGrace, lateWindow, fadeOut, episodeStatus);
            }

            return new TopOfHourGuard(package!.TargetUtc, introGrace, lateWindow, fadeOut, package.Status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not evaluate top-of-hour playout guard");
            return null;
        }
    }

    public async Task WriteTransitionLogAsync(
        PlayoutItem outgoing, PlayoutItem incoming, TransitionPlan plan, int clipCount, int underruns)
    {
        try
        {
            // Deliberately not tied to the session token: a transition that already
            // aired should still be recorded even while the session is tearing down.
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.TransitionLog.Add(new TransitionLogEntry
            {
                OccurredAt = DateTime.UtcNow,
                OutgoingType = outgoing.ItemType,
                OutgoingId = outgoing.ItemId,
                IncomingType = incoming.ItemType,
                IncomingId = incoming.ItemId,
                Strategy = plan.Strategy.ToString(),
                OverlapSeconds = plan.OverlapSeconds,
                GapMs = plan.GapMs,
                ParametersJson = JsonSerializer.Serialize(new
                {
                    reasonTrace = plan.ReasonTrace,
                    duckLevelDb = plan.DuckLevelDb,
                    incomingStartOffsetSeconds = plan.IncomingStartOffsetSeconds,
                    underruns,
                }),
                ClipCount = clipCount,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            mixerUpdates.Publish();
            metrics.MixerTransition(plan.Strategy.ToString(), clipCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write transition log entry");
        }
    }
}
