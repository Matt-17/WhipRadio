using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

/// <summary>
/// Pins the item→schedule math extracted from AudioMixerEngine: file start offsets,
/// end positions on the master clock, and the IntroTalkOver composite envelopes.
/// </summary>
[TestClass]
public class SourceSchedulerTests
{
    private static readonly PcmFormat Format = new();

    private static PlayoutItem Track(double seconds, double startOffset = 0)
        => new(PlayoutItemType.Track, Guid.NewGuid(), "t.wav", "track", seconds, StartOffsetSeconds: startOffset);

    private static PlayoutItem Talk(double seconds)
        => new(PlayoutItemType.Announcement, Guid.NewGuid(), "a.wav", "talk", seconds);

    [TestMethod]
    public void PlaybackStartSeconds_SkipsLeadingSilenceForTracksOnly()
    {
        var analysis = new MediaAnalysis { AnalyzerVersion = 1, LeadingSilenceSeconds = 3 };
        var trackInfo = new ItemInfo(PlayoutItemType.Track, analysis, 100);
        var talkInfo = new ItemInfo(PlayoutItemType.Announcement, analysis, 100);

        Assert.Equal(3.0, SourceScheduler.PlaybackStartSeconds(Track(100), trackInfo));
        Assert.Equal(0.0, SourceScheduler.PlaybackStartSeconds(Talk(100), talkInfo));

        // A resume offset beyond the silence wins; offsets clamp to the duration.
        Assert.Equal(10.0, SourceScheduler.PlaybackStartSeconds(Track(100, startOffset: 10), trackInfo));
        Assert.Equal(100.0, SourceScheduler.PlaybackStartSeconds(Track(100, startOffset: 150), trackInfo));
        Assert.Equal(3.0, SourceScheduler.PlaybackStartSeconds(Track(100, startOffset: double.NaN), trackInfo));
    }

    [TestMethod]
    public void PlanFullLevel_SchedulesEndFromRemainingDuration()
    {
        var info = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 10);
        var startAt = Format.SecondsToSamples(1);

        var scheduled = SourceScheduler.PlanFullLevel(Track(10), info, new MixerSettings(), Format, startAt, reportAt: startAt);

        Assert.Equal(startAt, scheduled.StartAtMaster);
        Assert.Equal(startAt + Format.SecondsToSamples(10), scheduled.EndAtMaster);
        Assert.Equal(startAt, scheduled.ReportAtMaster);
        Assert.Equal(0.0, scheduled.SourceStartSeconds);
        Assert.Equal(1f, scheduled.MakeupGainLinear, 3); // no analysis → no makeup
        Assert.Equal(0f, scheduled.Envelope.GainAt(startAt), 3);
        Assert.Equal(1f, scheduled.Envelope.GainAt(startAt + Format.SecondsToSamples(5)), 3);
        Assert.Equal(0f, scheduled.Envelope.GainAt(scheduled.EndAtMaster), 3);
    }

    [TestMethod]
    public void PlanFullLevel_ResumeOffsetShortensTheRemainingPlayback()
    {
        var info = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 10);

        var scheduled = SourceScheduler.PlanFullLevel(
            Track(10, startOffset: 4), info, new MixerSettings(), Format, startAt: 0, reportAt: 0);

        Assert.Equal(4.0, scheduled.SourceStartSeconds);
        Assert.Equal(Format.SecondsToSamples(6), scheduled.EndAtMaster);
    }

    [TestMethod]
    public void PlanIntroTalkOver_DucksTheSongBed_AndReportsItOnceTheTalkClears()
    {
        var settings = new MixerSettings(); // duck -12 dB, 800 ms ramp
        var talk = Talk(5);
        var talkInfo = new ItemInfo(PlayoutItemType.Announcement, Analysis: null, DurationSeconds: 5);
        var song = Track(100);
        var songInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 100);
        var plan = new TransitionPlan(
            MixStrategy.IntroTalkOver, OverlapSeconds: 0, GapMs: 0,
            IncomingStartOffsetSeconds: 2, DuckLevelDb: settings.DuckLevelDb, ReasonTrace: "test");

        var (scheduledSong, scheduledTalk) = SourceScheduler.PlanIntroTalkOver(
            talk, talkInfo, song, songInfo, introEndSeconds: 20, plan, masterPos: 0, settings, Format);

        // Talk: starts at the planned offset into the bed, full level, reports immediately.
        Assert.Equal(Format.SecondsToSamples(2), scheduledTalk.StartAtMaster);
        Assert.Equal(Format.SecondsToSamples(7), scheduledTalk.EndAtMaster);
        Assert.Equal(scheduledTalk.StartAtMaster, scheduledTalk.ReportAtMaster);
        Assert.Equal(1f, scheduledTalk.Envelope.GainAt(Format.SecondsToSamples(4)), 3);

        // Song bed: runs to its own end; "audible" report waits for the duck release
        // at IntroEnd (later than the talk end here).
        Assert.Equal(0L, scheduledSong.StartAtMaster);
        Assert.Equal(Format.SecondsToSamples(100), scheduledSong.EndAtMaster);
        Assert.Equal(Format.SecondsToSamples(20), scheduledSong.ReportAtMaster);

        var duckGain = TransitionMath.DbToLinear(settings.DuckLevelDb);
        Assert.Equal(duckGain, scheduledSong.Envelope.GainAt(0), 3);
        Assert.Equal(1f, scheduledSong.Envelope.GainAt(Format.SecondsToSamples(20)), 3);
        Assert.Equal(1f, scheduledSong.Envelope.GainAt(Format.SecondsToSamples(60)), 3);
        Assert.Equal(0f, scheduledSong.Envelope.GainAt(scheduledSong.EndAtMaster), 3);
    }
}
