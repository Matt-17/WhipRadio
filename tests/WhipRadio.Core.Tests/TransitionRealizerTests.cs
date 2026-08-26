using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

/// <summary>
/// Pins the plan→envelope realization extracted from AudioMixerEngine: fade
/// breakpoints, beat alignment, outro ducking, and the documented degradations
/// when a planned strategy meets an item without the analysis data it needs.
/// </summary>
[TestClass]
public class TransitionRealizerTests
{
    private static readonly PcmFormat Format = new();
    private static readonly MixerSettings Settings = new(); // crossfade 5 s, duck -12 dB / 800 ms

    private static PlayoutItem Track(double seconds)
        => new(PlayoutItemType.Track, Guid.NewGuid(), "t.wav", "track", seconds);

    private static PlayoutItem Talk(double seconds)
        => new(PlayoutItemType.Announcement, Guid.NewGuid(), "a.wav", "talk", seconds);

    private static TransitionPlan Plan(MixStrategy strategy, double overlap = 0, int gapMs = 0)
        => new(strategy, overlap, gapMs, IncomingStartOffsetSeconds: null,
            DuckLevelDb: Settings.DuckLevelDb, ReasonTrace: "test");

    private static (GainEnvelope Envelope, long End) OutgoingAtFullLevel(double durationSeconds)
    {
        var end = Format.SecondsToSamples(durationSeconds);
        return (EnvelopeFactory.FullLevel(Format, 0, end), end);
    }

    [TestMethod]
    public void EnergyFade_ReplacesTheOutgoingEnding_WithAnEqualPowerCrossfade()
    {
        var outgoingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 100);
        var incomingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 80);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.EnergyFade, overlap: 4), outgoingInfo, envelope, outgoingEnd,
            Track(80), incomingInfo, Settings, Format);

        var fadeStart = outgoingEnd - Format.SecondsToSamples(4);
        Assert.Equal(TransitionFallback.None, realization.Fallback);
        Assert.Equal(outgoingEnd, realization.OutgoingEndAtMaster);
        Assert.Equal(outgoingEnd, realization.LogCompleteAtMaster);

        // Equal-power out: full at fade start, ~0.707 at the midpoint, silent at the end.
        Assert.Equal(1f, envelope.GainAt(fadeStart), 3);
        Assert.Equal(0.707f, envelope.GainAt(fadeStart + Format.SecondsToSamples(2)), 2);
        Assert.Equal(0f, envelope.GainAt(outgoingEnd), 3);

        Assert.Equal(fadeStart, realization.Incoming.StartAtMaster);
        Assert.Equal(fadeStart + Format.SecondsToSamples(80), realization.Incoming.EndAtMaster);
        Assert.Equal((fadeStart + outgoingEnd) / 2, realization.Incoming.ReportAtMaster);
    }

    [TestMethod]
    public void BeatAlignedFade_LandsTheIncomingFirstBeat_OnTheChosenOutgoingBeat()
    {
        var outgoingInfo = new ItemInfo(
            PlayoutItemType.Track,
            new MediaAnalysis
            {
                AnalyzerVersion = 1,
                Bpm = 120,
                BeatGridJson = "[89.5, 90.0, 90.5]",
                OutroStartSeconds = 90,
                OutroConfidence = 0.9,
            },
            DurationSeconds: 100);
        var incomingInfo = new ItemInfo(
            PlayoutItemType.Track,
            new MediaAnalysis { AnalyzerVersion = 1, BeatGridJson = "[0.5, 1.0]" },
            DurationSeconds: 80);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.BeatAlignedFade, overlap: 4), outgoingInfo, envelope, outgoingEnd,
            Track(80), incomingInfo, Settings, Format);

        // Anchor = outro (90 s, confident); nearest beat 90.0; 5 s crossfade at 120 bpm
        // = 10 beats = 5 s of overlap.
        var fadeStart = Format.SecondsToSamples(90);
        var fadeEnd = fadeStart + Format.SecondsToSamples(5);
        Assert.Equal(TransitionFallback.None, realization.Fallback);
        Assert.Equal(fadeEnd, realization.OutgoingEndAtMaster);

        // The incoming starts so its first beat (0.5 s in) lands exactly on the fade start.
        Assert.Equal(fadeStart - Format.SecondsToSamples(0.5), realization.Incoming.StartAtMaster);
        Assert.Equal(1f, envelope.GainAt(fadeStart), 3);
        Assert.Equal(0f, envelope.GainAt(fadeEnd), 3);
    }

    [TestMethod]
    public void BeatAlignedFade_WithoutBeatData_DegradesToAPlainCrossfade()
    {
        var outgoingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 100);
        var incomingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 80);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.BeatAlignedFade, overlap: 3), outgoingInfo, envelope, outgoingEnd,
            Track(80), incomingInfo, Settings, Format);

        Assert.Equal(TransitionFallback.BeatDataMissing, realization.Fallback);
        Assert.Equal(outgoingEnd - Format.SecondsToSamples(3), realization.Incoming.StartAtMaster);
        Assert.Equal(outgoingEnd, realization.OutgoingEndAtMaster);
    }

    [TestMethod]
    public void OutroTalkOver_DucksTheSongUnderTheTalk_AndLeavesItsEndUnchanged()
    {
        var outgoingInfo = new ItemInfo(
            PlayoutItemType.Track,
            new MediaAnalysis { AnalyzerVersion = 1, OutroStartSeconds = 85, IntegratedLufs = Settings.TargetLufs },
            DurationSeconds: 100);
        var incomingInfo = new ItemInfo(PlayoutItemType.Announcement, Analysis: null, DurationSeconds: 10);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.OutroTalkOver), outgoingInfo, envelope, outgoingEnd,
            Talk(10), incomingInfo, Settings, Format);

        var talkStart = Format.SecondsToSamples(85);
        var duckGain = TransitionMath.DbToLinear(Settings.DuckLevelDb);
        Assert.Equal(TransitionFallback.None, realization.Fallback);
        Assert.Null(realization.OutgoingEndAtMaster); // the song still ends as planned
        Assert.Equal(outgoingEnd, realization.LogCompleteAtMaster);

        Assert.Equal(duckGain, envelope.GainAt(talkStart), 3);
        Assert.Equal(duckGain, envelope.GainAt(Format.SecondsToSamples(92)), 3);
        Assert.Equal(0f, envelope.GainAt(outgoingEnd), 3);

        Assert.Equal(talkStart, realization.Incoming.StartAtMaster);
        Assert.Equal(talkStart, realization.Incoming.ReportAtMaster);
        Assert.Equal(talkStart + Format.SecondsToSamples(10), realization.Incoming.EndAtMaster);
    }

    [TestMethod]
    public void OutroTalkOver_WithoutOutroData_DegradesToAHardCut()
    {
        var outgoingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 100);
        var incomingInfo = new ItemInfo(PlayoutItemType.Announcement, Analysis: null, DurationSeconds: 10);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.OutroTalkOver, gapMs: 300), outgoingInfo, envelope, outgoingEnd,
            Talk(10), incomingInfo, Settings, Format);

        Assert.Equal(TransitionFallback.OutroDataMissing, realization.Fallback);
        Assert.Null(realization.OutgoingEndAtMaster);
        Assert.Equal(outgoingEnd + Format.SecondsToSamples(0.3), realization.Incoming.StartAtMaster);
    }

    [TestMethod]
    public void HardCut_StartsTheIncomingAfterTheGap()
    {
        var outgoingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 100);
        var incomingInfo = new ItemInfo(PlayoutItemType.Track, Analysis: null, DurationSeconds: 80);
        var (envelope, outgoingEnd) = OutgoingAtFullLevel(100);

        var realization = TransitionRealizer.Realize(
            Plan(MixStrategy.HardCut, gapMs: 500), outgoingInfo, envelope, outgoingEnd,
            Track(80), incomingInfo, Settings, Format);

        var expectedStart = outgoingEnd + Format.SecondsToSamples(0.5);
        Assert.Equal(TransitionFallback.None, realization.Fallback);
        Assert.Null(realization.OutgoingEndAtMaster);
        Assert.Equal(expectedStart, realization.Incoming.StartAtMaster);
        Assert.Equal(expectedStart, realization.Incoming.ReportAtMaster);
        Assert.Equal(expectedStart, realization.LogCompleteAtMaster);
        Assert.Equal(1f, realization.Incoming.Envelope.GainAt(expectedStart + Format.SecondsToSamples(1)), 3);
    }
}
