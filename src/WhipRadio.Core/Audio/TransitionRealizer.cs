using System.Text.Json;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Core.Audio;

/// <summary>Degradations applied when a planned strategy meets an item without
/// the analysis data it needs; the caller decides how to log them.</summary>
public enum TransitionFallback
{
    None,

    /// <summary>BeatAlignedFade planned but beat data missing/invalid → plain crossfade.</summary>
    BeatDataMissing,

    /// <summary>OutroTalkOver planned but OutroStart missing → hard cut.</summary>
    OutroDataMissing,
}

/// <summary>
/// The realized transition: the outgoing envelope has been edited in place;
/// <paramref name="OutgoingEndAtMaster"/> is set when the fade replaced the
/// outgoing item's planned ending, and the incoming source is fully scheduled.
/// <paramref name="LogCompleteAtMaster"/> is the master position at which the
/// transition counts as aired (for the transition log).
/// </summary>
public sealed record TransitionRealization(
    long? OutgoingEndAtMaster,
    ScheduledSource Incoming,
    long LogCompleteAtMaster,
    TransitionFallback Fallback);

/// <summary>
/// Realises a <see cref="TransitionPlan"/> into envelope breakpoints and an
/// incoming-source schedule. Pure sample math — no I/O, no clock: the engine
/// resolves item analysis and attaches readers.
/// </summary>
public static class TransitionRealizer
{
    public static TransitionRealization Realize(
        TransitionPlan plan,
        ItemInfo outgoingInfo, GainEnvelope outgoingEnvelope, long outgoingEndAtMaster,
        PlayoutItem incoming, ItemInfo incomingInfo,
        MixerSettings settings, PcmFormat format)
    {
        var rate = format.SampleRate;
        var outgoingEnd = outgoingEndAtMaster;
        var leadIn = SourceScheduler.PlaybackStartSeconds(incoming, incomingInfo);
        var fallback = TransitionFallback.None;

        long? newOutgoingEnd = null;
        long incomingStart;
        long reportAt;
        GainEnvelope incomingEnvelope;
        long incomingEnd;
        long logCompleteAt;

        switch (plan.Strategy)
        {
            case MixStrategy.EnergyFade:
            case MixStrategy.OutroBridgeIn:
            case MixStrategy.BeatAlignedFade:
            {
                var overlapSamples = format.SecondsToSamples(plan.OverlapSeconds);
                var fadeStart = outgoingEnd - overlapSamples;
                var fadeEnd = outgoingEnd;

                if (plan.Strategy == MixStrategy.BeatAlignedFade
                    && outgoingInfo.Analysis is { Bpm: { } bpmOut, BeatGridJson: { } beatGridOutJson }
                    && incomingInfo.Analysis is { BeatGridJson: { } beatGridInJson }
                    && TryParseBeatGrid(beatGridOutJson, out var beatsOut)
                    && TryParseBeatGrid(beatGridInJson, out var beatsIn))
                {
                    var anchorSeconds = outgoingInfo.Analysis.OutroConfidence >= 0.5
                            && outgoingInfo.Analysis.OutroStartSeconds is { } outro
                        ? outro
                        : outgoingInfo.DurationSeconds - settings.DefaultCrossfadeSeconds;
                    var beatOut = TransitionMath.NearestBeat(beatsOut, anchorSeconds);
                    var beats = TransitionMath.CrossfadeBeats(settings.DefaultCrossfadeSeconds, bpmOut);
                    var overlapSeconds = beats * 60.0 / bpmOut;

                    var outgoingItemStart = outgoingEnd - format.SecondsToSamples(outgoingInfo.DurationSeconds);
                    fadeStart = outgoingItemStart + format.SecondsToSamples(beatOut);
                    fadeEnd = fadeStart + format.SecondsToSamples(overlapSeconds);

                    var firstAudibleBeat = beatsIn.Length > 0 ? Math.Max(0, beatsIn[0] - leadIn) : 0;
                    incomingStart = TransitionMath.IncomingStartMasterSample(fadeStart, firstAudibleBeat, rate);
                }
                else
                {
                    if (plan.Strategy == MixStrategy.BeatAlignedFade)
                    {
                        fallback = TransitionFallback.BeatDataMissing;
                    }

                    incomingStart = fadeStart;
                }

                // Replace the outgoing item's planned ending with the fade.
                outgoingEnvelope.RemoveBreakpointsFrom(fadeStart);
                outgoingEnvelope.AddBreakpoint(fadeStart, 1f, RampShape.EqualPowerOut);
                outgoingEnvelope.AddBreakpoint(fadeEnd, 0f, RampShape.Hold);
                newOutgoingEnd = fadeEnd;

                incomingEnd = incomingStart + format.SecondsToSamples(SourceScheduler.RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FadeIn(format, Math.Max(incomingStart, fadeStart), fadeEnd, incomingEnd);
                reportAt = (fadeStart + fadeEnd) / 2; // crossfade midpoint
                logCompleteAt = fadeEnd;
                break;
            }

            case MixStrategy.OutroTalkOver:
            {
                if (outgoingInfo.Analysis?.OutroStartSeconds is not { } outroStartSeconds)
                {
                    fallback = TransitionFallback.OutroDataMissing;
                    goto default;
                }

                var outgoingItemStart = outgoingEnd - format.SecondsToSamples(outgoingInfo.DurationSeconds);
                var talkStart = outgoingItemStart + format.SecondsToSamples(outroStartSeconds);
                var duckRamp = format.SecondsToSamples(settings.DuckRampMs / 1000.0);
                var duckGain = TransitionMath.DbToLinear(settings.DuckLevelDb);

                // Duck the song under the talk; it ends (under talk) as planned.
                outgoingEnvelope.RemoveBreakpointsFrom(talkStart - duckRamp);
                outgoingEnvelope.AddBreakpoint(talkStart - duckRamp, 1f, RampShape.Linear);
                outgoingEnvelope.AddBreakpoint(talkStart, duckGain, RampShape.Hold);
                outgoingEnvelope.AddBreakpoint(
                    Math.Max(talkStart, outgoingEnd - EnvelopeFactory.RampSamples(format)), duckGain, RampShape.Linear);
                outgoingEnvelope.AddBreakpoint(outgoingEnd, 0f, RampShape.Hold);

                incomingStart = talkStart;
                incomingEnd = incomingStart + format.SecondsToSamples(SourceScheduler.RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FullLevel(format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                logCompleteAt = outgoingEnd;
                break;
            }

            default: // HardCut (and IntroTalkOver never reaches here: planned at item start)
            {
                incomingStart = outgoingEnd + format.SecondsToSamples(plan.GapMs / 1000.0);
                incomingEnd = incomingStart + format.SecondsToSamples(SourceScheduler.RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FullLevel(format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                logCompleteAt = incomingStart;
                break;
            }
        }

        var incomingScheduled = new ScheduledSource(
            incomingStart,
            incomingEnd,
            reportAt,
            leadIn,
            SourceScheduler.Makeup(incomingInfo, settings),
            incomingEnvelope);

        return new TransitionRealization(newOutgoingEnd, incomingScheduled, logCompleteAt, fallback);
    }

    private static bool TryParseBeatGrid(string json, out double[] beats)
    {
        try
        {
            beats = JsonSerializer.Deserialize<double[]>(json) ?? [];
            return true;
        }
        catch (JsonException)
        {
            beats = [];
            return false;
        }
    }
}
