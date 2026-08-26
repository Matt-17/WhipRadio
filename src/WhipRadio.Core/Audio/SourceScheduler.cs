using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Audio;

/// <summary>
/// A source scheduled on the master clock — everything the mixer needs to put an
/// item on air except the PCM reader itself, which the engine attaches.
/// </summary>
public sealed record ScheduledSource(
    long StartAtMaster,
    long EndAtMaster,
    long ReportAtMaster,
    double SourceStartSeconds,
    float MakeupGainLinear,
    GainEnvelope Envelope);

/// <summary>
/// Pure item→schedule math: where an item starts inside its file, when it ends on
/// the master clock, and the envelopes for plain playback and the IntroTalkOver
/// composite (song bed ducked under the talk across the intro).
/// </summary>
public static class SourceScheduler
{
    /// <summary>Plain playback at full level from <paramref name="startAt"/>.</summary>
    public static ScheduledSource PlanFullLevel(
        PlayoutItem item, ItemInfo info, MixerSettings settings, PcmFormat format, long startAt, long reportAt)
    {
        var startOffset = PlaybackStartSeconds(item, info);
        var end = startAt + format.SecondsToSamples(RemainingSeconds(info, startOffset));
        return new ScheduledSource(
            startAt,
            end,
            reportAt,
            startOffset,
            Makeup(info, settings),
            EnvelopeFactory.FullLevel(format, startAt, end));
    }

    /// <summary>
    /// IntroTalkOver composite: the song bed starts now, ducked under the talk; the
    /// duck release ramp ends exactly at the song's IntroEnd ("hit the post").
    /// </summary>
    public static (ScheduledSource Song, ScheduledSource Talk) PlanIntroTalkOver(
        PlayoutItem talk, ItemInfo talkInfo, PlayoutItem song, ItemInfo songInfo, double introEndSeconds,
        TransitionPlan plan, long masterPos, MixerSettings settings, PcmFormat format)
    {
        var talkStartOffset = plan.IncomingStartOffsetSeconds ?? 0;
        var songStartOffsetSeconds = PlaybackStartSeconds(song, songInfo);
        var talkPlaybackStartSeconds = PlaybackStartSeconds(talk, talkInfo);
        var songStart = masterPos;
        var talkStart = songStart + format.SecondsToSamples(talkStartOffset);
        var talkEnd = talkStart + format.SecondsToSamples(RemainingSeconds(talkInfo, talkPlaybackStartSeconds));
        var songEnd = songStart + format.SecondsToSamples(RemainingSeconds(songInfo, songStartOffsetSeconds));
        var duckReleaseEnd = songStart + format.SecondsToSamples(introEndSeconds);

        // Song bed: ducked under the talk; release ramp ENDS exactly at IntroEnd.
        var songEnvelope = EnvelopeFactory.DuckedBed(
            format, songStart, songEnd,
            duckStartSample: songStart,
            duckEndSample: Math.Max(talkEnd, duckReleaseEnd),
            settings.DuckLevelDb, settings.DuckRampMs);
        var scheduledSong = new ScheduledSource(
            songStart,
            songEnd,
            Math.Max(talkEnd, duckReleaseEnd), // song "audible" once the talk clears
            songStartOffsetSeconds,
            Makeup(songInfo, settings),
            songEnvelope);

        var scheduledTalk = new ScheduledSource(
            talkStart,
            talkEnd,
            talkStart,
            talkPlaybackStartSeconds,
            Makeup(talkInfo, settings),
            EnvelopeFactory.FullLevel(format, talkStart, talkEnd));

        return (scheduledSong, scheduledTalk);
    }

    public static double PlaybackStartSeconds(PlayoutItem item, ItemInfo info)
    {
        var duration = Math.Max(0, info.DurationSeconds);
        var resumeOffset = Math.Clamp(double.IsFinite(item.StartOffsetSeconds) ? item.StartOffsetSeconds : 0, 0, duration);
        // LeadingSilenceSeconds is meaningful for tracks (skip silent intros) but NOT for
        // announcements — a speech analysis over-reporting silence would seek near EOF and
        // leave almost nothing to play.
        var leadIn = item.ItemType == PlayoutItemType.Announcement
            ? 0
            : Math.Clamp(info.Analysis?.LeadingSilenceSeconds ?? 0, 0, duration);
        return Math.Max(resumeOffset, leadIn);
    }

    public static double RemainingSeconds(ItemInfo info, double startOffsetSeconds)
        => Math.Max(0, info.DurationSeconds - startOffsetSeconds);

    public static float Makeup(ItemInfo info, MixerSettings settings)
        => TransitionMath.MakeupGainLinear(
            info.Analysis is { AnalyzerVersion: > 0 } a ? a.IntegratedLufs : null,
            settings.TargetLufs, settings.MaxMakeupGainDb);
}
