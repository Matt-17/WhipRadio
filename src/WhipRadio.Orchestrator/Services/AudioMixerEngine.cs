using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The real-time mixer session (Phase 3a): owns the master sample clock, feeds
/// summed PCM into the existing encoder pipe, and realises TransitionPlans as
/// overlapping SourceSlots. Runs INSTEAD of the legacy per-item copy loop while
/// MixerEnabled=true; returns at an item boundary when the flag flips off.
/// </summary>
public sealed class AudioMixerEngine(
    IPlayoutQueue queue,
    IPlaybackReporter reporter,
    PlayoutStateStore stateStore,
    TrackDeletionService trackDeletions,
    IMixPlanner planner,
    MixerDiagnostics diagnostics,
    IMixerUpdatePublisher mixerUpdates,
    TimedPlayoutInterruptService timedInterrupts,
    IPcmSampleReaderFactory readerFactory,
    IStationMetrics metrics,
    IDbContextFactory<RadioDbContext> dbFactory,
    ILogger<AudioMixerEngine> logger)
{
    private static readonly PcmFormat Format = new();

    private sealed class ActiveSource
    {
        public required SourceSlot Slot { get; init; }

        public required PlayoutItem Item { get; init; }

        public required IPcmSampleReader Reader { get; init; }

        public required long EndAtMaster { get; set; }

        public long ReportAtMaster { get; set; }

        public bool Reported { get; set; }
    }

    private sealed record PendingLog(
        PlayoutItem Outgoing, PlayoutItem Incoming, TransitionPlan Plan, int ClipBaseline, long CompleteAtMaster);

    private sealed record TopOfHourGuard(
        DateTime TargetUtc,
        int IntroGraceSeconds,
        int LateWindowSeconds,
        double FadeOutSeconds,
        NewsPackageStatus Status);

    /// <summary>Runs until cancelled, the encoder dies, or the mixer/playout flag
    /// turns the session off (returns at an item boundary).</summary>
    public async Task RunSessionAsync(
        IMixerEncoderSink encoder, Stream encoderInput,
        Func<CancellationToken, Task<bool>> sessionStillWanted, CancellationToken ct)
    {
        var core = new MixerCore(Format);
        var actives = new List<ActiveSource>(4);
        var outputShorts = new short[PcmFormat.FrameSamples * Format.Channels];
        var outputBytes = new byte[outputShorts.Length * 2];
        var accumulator = new float[outputShorts.Length];
        var scratch = new short[outputShorts.Length];

        long masterPos = 0;
        var framesSinceCheck = 0;
        var stopScheduling = false;
        var transitionPlanned = false;
        var pendingLogs = new List<PendingLog>();
        MixerSettings settings = await LoadSettingsAsync(ct);
        TopOfHourGuard? topOfHourGuard = await GetTopOfHourGuardAsync(DateTime.UtcNow, TimeSpan.Zero, ct);

        diagnostics.SessionStarted();
        mixerUpdates.Publish();
        logger.LogInformation(
            "Mixer session started: target {Lufs} LUFS, crossfade {Fade}s, duck {Duck} dB, talk gap {GMin}-{GMax} ms",
            settings.TargetLufs, settings.DefaultCrossfadeSeconds, settings.DuckLevelDb,
            settings.HardCutGapAfterTalkMsMin, settings.HardCutGapAfterTalkMsMax);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (encoder.HasExited)
                {
                    throw new InvalidOperationException($"Encoder ffmpeg exited with code {encoder.ExitCode}.");
                }

                // Flag checks + live diagnostics every ~2 s of audio.
                if (++framesSinceCheck >= 86)
                {
                    framesSinceCheck = 0;
                    if (!await sessionStillWanted(ct))
                    {
                        stopScheduling = true; // finish what's playing, then hand back
                    }

                    topOfHourGuard = await GetTopOfHourGuardAsync(DateTime.UtcNow, TimeSpan.Zero, ct);
                    PublishLive(masterPos, actives);
                }

                if (!stopScheduling && timedInterrupts.TryConsume(DateTime.UtcNow) is { } interrupt)
                {
                    settings = await LoadSettingsAsync(ct);
                    await ApplyTimedInterruptAsync(interrupt, masterPos, actives, settings, ct);
                    PublishLive(masterPos, actives);
                    transitionPlanned = true;
                }

                if (!stopScheduling && topOfHourGuard is { } holdGuard)
                {
                    if (ApplyTopOfHourHoldFade(holdGuard, masterPos, actives))
                    {
                        PublishLive(masterPos, actives);
                    }
                }

                if (actives.Count == 0)
                {
                    if (stopScheduling)
                    {
                        return;
                    }

                    // Nothing playing: pull the next item (or stream silence).
                    if (queue.PeekNext() is { ItemType: PlayoutItemType.Track }
                        && topOfHourGuard is { } guard)
                    {
                        logger.LogInformation(
                            "Mixer top-of-hour hold: not starting a track while package {Status} for {Target:u}",
                            guard.Status,
                            guard.TargetUtc);
                        await WriteFrameAsync(encoderInput, outputShorts, outputBytes, clear: true, ct);
                        masterPos += PcmFormat.FrameSamples;
                        continue;
                    }

                    var item = await TryDequeueAsync(TimeSpan.FromSeconds(1), ct);
                    if (item is null)
                    {
                        await WriteFrameAsync(encoderInput, outputShorts, outputBytes, clear: true, ct);
                        masterPos += PcmFormat.FrameSamples;
                        continue;
                    }

                    settings = await LoadSettingsAsync(ct);
                    await StartItemChainAsync(item, masterPos, actives, settings, ct);
                    PublishLive(masterPos, actives);
                    transitionPlanned = false;
                }

                // Lookahead: when the LAST scheduled item is close to its end and
                // no transition is planned yet, plan + pre-spawn the incoming.
                if (!stopScheduling && !transitionPlanned && actives.Count > 0)
                {
                    var current = actives[^1];
                    var remaining = (current.EndAtMaster - masterPos) / (double)Format.SampleRate;
                    var window = Math.Max(settings.DefaultCrossfadeSeconds, 2) + 10;
                    if (remaining <= window && queue.PeekNext() is not null)
                    {
                        if (queue.PeekNext() is { ItemType: PlayoutItemType.Track }
                            && await GetTopOfHourGuardAsync(
                                DateTime.UtcNow,
                                TimeSpan.FromSeconds(Math.Max(remaining, 0) + window),
                                ct) is { } guard)
                        {
                            logger.LogInformation(
                                "Mixer top-of-hour hold: not transitioning into a track while package {Status} for {Target:u}",
                                guard.Status,
                                guard.TargetUtc);
                        }
                        else
                        {
                            var incoming = await TryDequeueAsync(TimeSpan.FromMilliseconds(50), ct);
                            if (incoming is not null)
                            {
                                settings = await LoadSettingsAsync(ct);
                                await ApplyTransitionAsync(current, incoming, actives, settings, core, pendingLogs, ct);
                                PublishLive(masterPos, actives);
                                transitionPlanned = true;
                            }
                        }
                    }
                }

                core.ResetCountersIfIdle(actives.Count == 0);
                MixAndEmit(core, actives, masterPos, outputShorts, accumulator, scratch);
                await WriteFrameAsync(encoderInput, outputShorts, outputBytes, clear: false, ct);
                masterPos += PcmFormat.FrameSamples;

                await EmitDueEventsAsync(actives, masterPos, ct);
                await FlushDueLogsAsync(core, pendingLogs, masterPos, ct);

                // Cleanup: finished (EOF) or envelope-complete sources.
                for (var i = actives.Count - 1; i >= 0; i--)
                {
                    var a = actives[i];
                    if (a.Slot.Finished || masterPos > a.EndAtMaster)
                    {
                        // A source that hits EOF well before its planned end means
                        // the FILE is shorter than its duration metadata — the
                        // "is the song broken or is it the mixer?" question, answered.
                        var earlySeconds = Format.SamplesToSeconds(a.EndAtMaster - masterPos);
                        if (a.Slot.Finished && earlySeconds > 2)
                        {
                            logger.LogWarning(
                                "Mixer: \"{Title}\" audio ended {Early:F1}s before its expected end — "
                                + "file shorter than duration metadata (broken/truncated file?)",
                                a.Item.Title, earlySeconds);
                        }

                        a.Reader.DisposeIfDisposable();
                        stateStore.Complete(a.Item);
                        await trackDeletions.MarkPlaybackCompletedAsync(a.Item, ct);
                        actives.RemoveAt(i);
                        if (actives.Count > 0 && i == actives.Count)
                        {
                            transitionPlanned = false; // the survivor is the new "current"
                        }
                    }
                }

                if (actives.Count == 0)
                {
                    transitionPlanned = false;
                }
            }
        }
        finally
        {
            diagnostics.SessionEnded();
            mixerUpdates.Publish();
            foreach (var active in actives)
            {
                active.Reader.DisposeIfDisposable();
                stateStore.Complete(active.Item);
                await trackDeletions.MarkPlaybackCompletedAsync(active.Item, CancellationToken.None);
            }
        }
    }

    private void PublishLive(long masterPos, List<ActiveSource> actives)
    {
        diagnostics.Update(
            Format.SamplesToSeconds(masterPos),
            actives.Select(a => $"{a.Item.Title} [{DescribePhase(a, masterPos)}]"));
        mixerUpdates.Publish();
    }

    private static string DescribePhase(ActiveSource source, long masterPos)
    {
        if (masterPos < source.Slot.StartAtMasterSample)
        {
            return $"starts in {(source.Slot.StartAtMasterSample - masterPos) / (double)Format.SampleRate:F0}s";
        }

        var remaining = (source.EndAtMaster - masterPos) / (double)Format.SampleRate;
        return $"{remaining:F0}s left";
    }

    private static string DescribeAnalysis(ItemInfo info)
    {
        if (info.Analysis is not { AnalyzerVersion: > 0 } a)
        {
            return "NOT ANALYZED";
        }

        var parts = new List<string> { $"{a.IntegratedLufs:F1} LUFS" };
        if (a.Bpm is { } bpm)
        {
            parts.Add($"bpm {bpm:F0} c{a.BpmConfidence:F2}");
        }

        if (a.IntroEndSeconds is { } intro)
        {
            parts.Add($"intro {intro:F1}s c{a.IntroConfidence:F2}");
        }

        if (a.OutroStartSeconds is { } outro)
        {
            parts.Add($"outro {outro:F1}s c{a.OutroConfidence:F2}");
        }

        return string.Join(", ", parts);
    }

    // --- item scheduling ---------------------------------------------------------

    /// <summary>Starts an item; a talk followed by a song may become an
    /// IntroTalkOver composite (song bed + talk over its intro).</summary>
    private async Task StartItemChainAsync(
        PlayoutItem item, long masterPos, List<ActiveSource> actives, MixerSettings settings, CancellationToken ct)
    {
        if (item.ItemType == PlayoutItemType.Announcement && queue.PeekNext() is { ItemType: PlayoutItemType.Track })
        {
            var talkInfo = await BuildItemInfoAsync(item, ct);
            var peeked = queue.PeekNext()!;
            var songInfo = await BuildItemInfoAsync(peeked, ct);
            var plan = planner.Plan(talkInfo, songInfo, settings);

            logger.LogInformation(
                "Mixer decision: \"{Out}\" → \"{In}\" | in: {InAnalysis} | {Trace}",
                item.Title, peeked.Title, DescribeAnalysis(songInfo), plan.ReasonTrace);
            diagnostics.DecisionMade($"{item.Title} → {peeked.Title}: {plan.ReasonTrace}");
            mixerUpdates.Publish();

            if (plan.Strategy == MixStrategy.IntroTalkOver)
            {
                var song = await TryDequeueAsync(TimeSpan.FromMilliseconds(50), ct);
                if (song is not null)
                {
                    ScheduleIntroTalkOver(item, talkInfo, song, songInfo, plan, masterPos, actives, settings);
                    return;
                }
            }
        }

        var info = await BuildItemInfoAsync(item, ct);
        AddActiveSource(actives, CreateSource(item, info, masterPos, settings, EnvelopeKind.Full, reportAt: masterPos));
        logger.LogInformation(
            "Mixer: \"{Title}\" starts ({Duration:F0}s, {Analysis}, makeup {Makeup:F1} dB)",
            item.Title, info.DurationSeconds, DescribeAnalysis(info),
            20 * Math.Log10(Makeup(info, settings)));
    }

    private void ScheduleIntroTalkOver(
        PlayoutItem talk, ItemInfo talkInfo, PlayoutItem song, ItemInfo songInfo,
        TransitionPlan plan, long masterPos, List<ActiveSource> actives, MixerSettings settings)
    {
        var introEnd = songInfo.Analysis!.IntroEndSeconds!.Value;
        var talkStartOffset = plan.IncomingStartOffsetSeconds ?? 0;
        var songStartOffsetSeconds = PlaybackStartSeconds(song, songInfo);
        var talkPlaybackStartSeconds = PlaybackStartSeconds(talk, talkInfo);
        var songStart = masterPos;
        var talkStart = songStart + Format.SecondsToSamples(talkStartOffset);
        var talkEnd = talkStart + Format.SecondsToSamples(RemainingSeconds(talkInfo, talkPlaybackStartSeconds));
        var songEnd = songStart + Format.SecondsToSamples(RemainingSeconds(songInfo, songStartOffsetSeconds));
        var duckReleaseEnd = songStart + Format.SecondsToSamples(introEnd);

        // Song bed: ducked under the talk; release ramp ENDS exactly at IntroEnd.
        var songEnvelope = EnvelopeFactory.DuckedBed(
            Format, songStart, songEnd,
            duckStartSample: songStart,
            duckEndSample: Math.Max(talkEnd, duckReleaseEnd),
            settings.DuckLevelDb, settings.DuckRampMs);
        var songReader = CreateReader(song, songInfo, startAtSeconds: songStartOffsetSeconds);
        AddActiveSource(actives, new ActiveSource
        {
            Slot = new SourceSlot
            {
                Reader = songReader,
                Envelope = songEnvelope,
                StartAtMasterSample = songStart,
                MakeupGainLinear = Makeup(songInfo, settings),
            },
            Item = song,
            Reader = songReader,
            EndAtMaster = songEnd,
            ReportAtMaster = Math.Max(talkEnd, duckReleaseEnd), // song "audible" once the talk clears
        });

        var talkEnvelope = EnvelopeFactory.FullLevel(Format, talkStart, talkEnd);
        var talkReader = CreateReader(talk, talkInfo, startAtSeconds: talkPlaybackStartSeconds);
        AddActiveSource(actives, new ActiveSource
        {
            Slot = new SourceSlot
            {
                Reader = talkReader,
                Envelope = talkEnvelope,
                StartAtMasterSample = talkStart,
                MakeupGainLinear = Makeup(talkInfo, settings),
            },
            Item = talk,
            Reader = talkReader,
            EndAtMaster = talkEnd,
            ReportAtMaster = talkStart,
        });

        logger.LogInformation(
            "Mixer: IntroTalkOver — \"{Talk}\" over the intro of \"{Song}\" (post at {Intro:F1}s). {Trace}",
            talk.Title, song.Title, introEnd, plan.ReasonTrace);
    }

    // --- transitions ---------------------------------------------------------------

    private async Task ApplyTransitionAsync(
        ActiveSource outgoing, PlayoutItem incoming, List<ActiveSource> actives,
        MixerSettings settings, MixerCore core, List<PendingLog> pendingLogs, CancellationToken ct)
    {
        var outgoingInfo = await BuildItemInfoAsync(outgoing.Item, ct);
        var incomingInfo = await BuildItemInfoAsync(incoming, ct);
        var plan = planner.Plan(outgoingInfo, incomingInfo, settings);

        logger.LogInformation(
            "Mixer decision: \"{Out}\" → \"{In}\" | out: {OutAnalysis} | in: {InAnalysis} | {Trace}",
            outgoing.Item.Title, incoming.Title,
            DescribeAnalysis(outgoingInfo), DescribeAnalysis(incomingInfo), plan.ReasonTrace);
        diagnostics.DecisionMade($"{outgoing.Item.Title} → {incoming.Title}: {plan.ReasonTrace}");
        mixerUpdates.Publish();
        var rate = Format.SampleRate;
        var outgoingEnd = outgoing.EndAtMaster;
        var leadIn = PlaybackStartSeconds(incoming, incomingInfo);

        long incomingStart;
        long reportAt;
        GainEnvelope incomingEnvelope;
        long incomingEnd;

        switch (plan.Strategy)
        {
            case MixStrategy.EnergyFade:
            case MixStrategy.OutroBridgeIn:
            case MixStrategy.BeatAlignedFade:
            {
                var overlapSamples = Format.SecondsToSamples(plan.OverlapSeconds);
                var fadeStart = outgoingEnd - overlapSamples;
                var fadeEnd = outgoingEnd;

                if (plan.Strategy == MixStrategy.BeatAlignedFade)
                {
                    var beatsOut = JsonSerializer.Deserialize<double[]>(outgoingInfo.Analysis!.BeatGridJson!) ?? [];
                    var beatsIn = JsonSerializer.Deserialize<double[]>(incomingInfo.Analysis!.BeatGridJson!) ?? [];
                    var anchorSeconds = outgoingInfo.Analysis.OutroConfidence >= 0.5
                            && outgoingInfo.Analysis.OutroStartSeconds is { } outro
                        ? outro
                        : outgoingInfo.DurationSeconds - settings.DefaultCrossfadeSeconds;
                    var beatOut = TransitionMath.NearestBeat(beatsOut, anchorSeconds);
                    var beats = TransitionMath.CrossfadeBeats(
                        settings.DefaultCrossfadeSeconds, outgoingInfo.Analysis.Bpm!.Value);
                    var overlapSeconds = beats * 60.0 / outgoingInfo.Analysis.Bpm.Value;

                    var outgoingItemStart = outgoing.EndAtMaster - Format.SecondsToSamples(outgoingInfo.DurationSeconds);
                    fadeStart = outgoingItemStart + Format.SecondsToSamples(beatOut);
                    fadeEnd = fadeStart + Format.SecondsToSamples(overlapSeconds);

                    var firstAudibleBeat = beatsIn.Length > 0 ? Math.Max(0, beatsIn[0] - leadIn) : 0;
                    incomingStart = TransitionMath.IncomingStartMasterSample(fadeStart, firstAudibleBeat, rate);
                }
                else
                {
                    incomingStart = fadeStart;
                }

                // Replace the outgoing item's planned ending with the fade.
                outgoing.Slot.Envelope.RemoveBreakpointsFrom(fadeStart);
                outgoing.Slot.Envelope.AddBreakpoint(fadeStart, 1f, RampShape.EqualPowerOut);
                outgoing.Slot.Envelope.AddBreakpoint(fadeEnd, 0f, RampShape.Hold);
                outgoing.EndAtMaster = fadeEnd;

                incomingEnd = incomingStart + Format.SecondsToSamples(RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FadeIn(Format, Math.Max(incomingStart, fadeStart), fadeEnd, incomingEnd);
                reportAt = (fadeStart + fadeEnd) / 2; // crossfade midpoint
                pendingLogs.Add(new PendingLog(outgoing.Item, incoming, plan, core.ClipCount, fadeEnd));
                break;
            }

            case MixStrategy.OutroTalkOver:
            {
                var outgoingItemStart = outgoing.EndAtMaster - Format.SecondsToSamples(outgoingInfo.DurationSeconds);
                var talkStart = outgoingItemStart + Format.SecondsToSamples(
                    outgoingInfo.Analysis!.OutroStartSeconds!.Value);
                var duckRamp = Format.SecondsToSamples(settings.DuckRampMs / 1000.0);
                var duckGain = TransitionMath.DbToLinear(settings.DuckLevelDb);

                // Duck the song under the talk; it ends (under talk) as planned.
                outgoing.Slot.Envelope.RemoveBreakpointsFrom(talkStart - duckRamp);
                outgoing.Slot.Envelope.AddBreakpoint(talkStart - duckRamp, 1f, RampShape.Linear);
                outgoing.Slot.Envelope.AddBreakpoint(talkStart, duckGain, RampShape.Hold);
                outgoing.Slot.Envelope.AddBreakpoint(
                    Math.Max(talkStart, outgoingEnd - EnvelopeFactory.RampSamples(Format)), duckGain, RampShape.Linear);
                outgoing.Slot.Envelope.AddBreakpoint(outgoingEnd, 0f, RampShape.Hold);

                incomingStart = talkStart;
                incomingEnd = incomingStart + Format.SecondsToSamples(RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FullLevel(Format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                pendingLogs.Add(new PendingLog(outgoing.Item, incoming, plan, core.ClipCount, outgoingEnd));
                break;
            }

            default: // HardCut (and IntroTalkOver never reaches here: planned at item start)
            {
                incomingStart = outgoingEnd + Format.SecondsToSamples(plan.GapMs / 1000.0);
                incomingEnd = incomingStart + Format.SecondsToSamples(RemainingSeconds(incomingInfo, leadIn));
                incomingEnvelope = EnvelopeFactory.FullLevel(Format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                pendingLogs.Add(new PendingLog(outgoing.Item, incoming, plan, core.ClipCount, incomingStart));
                break;
            }
        }

        var reader = CreateReader(incoming, incomingInfo, startAtSeconds: leadIn);
        AddActiveSource(actives, new ActiveSource
        {
            Slot = new SourceSlot
            {
                Reader = reader,
                Envelope = incomingEnvelope,
                StartAtMasterSample = incomingStart,
                MakeupGainLinear = Makeup(incomingInfo, settings),
            },
            Item = incoming,
            Reader = reader,
            EndAtMaster = incomingEnd,
            ReportAtMaster = reportAt,
        });

        logger.LogInformation("Mixer transition: {Trace}", plan.ReasonTrace);
    }

    private async Task ApplyTimedInterruptAsync(
        TimedPlayoutInterrupt interrupt,
        long masterPos,
        List<ActiveSource> actives,
        MixerSettings settings,
        CancellationToken ct)
    {
        var fadeSamples = Format.SecondsToSamples(interrupt.FadeOutSeconds);
        var startAt = masterPos;
        if (actives.Count > 0)
        {
            var naturalEnd = actives
                .Where(source => source.EndAtMaster > masterPos)
                .Select(source => source.EndAtMaster)
                .DefaultIfEmpty(masterPos)
                .Max();
            var remainingSeconds = Format.SamplesToSeconds(naturalEnd - masterPos);
            var naturalEndUtc = DateTime.UtcNow.AddSeconds(remainingSeconds);
            if (TopOfHourScheduler.ShouldLetCurrentItemFinish(interrupt.TargetUtc, naturalEndUtc))
            {
                startAt = naturalEnd;
                logger.LogInformation(
                    "Mixer timed package: letting current audio finish ({Remaining:F1}s remaining) before {Title}",
                    remainingSeconds,
                    interrupt.Item.Title);
            }
            else
            {
                var secondsUntilTarget = Math.Max(0, (interrupt.TargetUtc - DateTime.UtcNow).TotalSeconds);
                var fadeStart = masterPos + Format.SecondsToSamples(secondsUntilTarget);
                var fadeEnd = fadeStart + Math.Max(1, fadeSamples);
                foreach (var active in actives.Where(source => source.EndAtMaster > masterPos))
                {
                    var currentGain = active.Slot.Envelope.GainAt(fadeStart);
                    active.Slot.Envelope.RemoveBreakpointsFrom(fadeStart);
                    active.Slot.Envelope.AddBreakpoint(fadeStart, currentGain, RampShape.Linear);
                    active.Slot.Envelope.AddBreakpoint(fadeEnd, 0f, RampShape.Hold);
                    active.EndAtMaster = Math.Min(active.EndAtMaster, fadeEnd);
                }

                startAt = fadeEnd;
            }
        }

        var info = await BuildItemInfoAsync(interrupt.Item, ct);
        AddActiveSource(actives, CreateSource(
            interrupt.Item,
            info,
            startAt,
            settings,
            EnvelopeKind.Full,
            reportAt: startAt));
        logger.LogInformation(
            "Mixer timed package: starting {Title} at {Delay:F1}s after decision",
            interrupt.Item.Title,
            Format.SamplesToSeconds(startAt - masterPos));
    }

    private bool ApplyTopOfHourHoldFade(
        TopOfHourGuard guard,
        long masterPos,
        List<ActiveSource> actives)
    {
        if (DateTime.UtcNow < guard.TargetUtc || actives.Count == 0)
        {
            return false;
        }

        var naturalEnd = actives
            .Where(source => source.EndAtMaster > masterPos)
            .Select(source => source.EndAtMaster)
            .DefaultIfEmpty(masterPos)
            .Max();
        var remainingSeconds = Format.SamplesToSeconds(naturalEnd - masterPos);
        var naturalEndUtc = DateTime.UtcNow.AddSeconds(remainingSeconds);
        if (TopOfHourScheduler.ShouldLetCurrentItemFinish(guard.TargetUtc, naturalEndUtc))
        {
            return false;
        }

        var fadeSamples = Format.SecondsToSamples(
            TopOfHourScheduler.NormalizeFadeOutSeconds(guard.FadeOutSeconds));
        var fadeEnd = masterPos + Math.Max(1, fadeSamples);
        foreach (var active in actives.Where(source => source.EndAtMaster > masterPos))
        {
            var currentGain = active.Slot.Envelope.GainAt(masterPos);
            active.Slot.Envelope.RemoveBreakpointsFrom(masterPos);
            active.Slot.Envelope.AddBreakpoint(masterPos, currentGain, RampShape.Linear);
            active.Slot.Envelope.AddBreakpoint(fadeEnd, 0f, RampShape.Hold);
            active.EndAtMaster = Math.Min(active.EndAtMaster, fadeEnd);
        }

        logger.LogInformation(
            "Mixer top-of-hour hold: fading current audio because package {Status} for {Target:u} is due",
            guard.Status,
            guard.TargetUtc);
        return true;
    }

    // --- helpers --------------------------------------------------------------------

    private enum EnvelopeKind
    {
        Full,
    }

    private void AddActiveSource(List<ActiveSource> actives, ActiveSource source)
    {
        actives.Add(source);
        trackDeletions.MarkPlaybackStarted(source.Item);
    }

    private ActiveSource CreateSource(
        PlayoutItem item, ItemInfo info, long startAt, MixerSettings settings, EnvelopeKind _, long reportAt)
    {
        var startOffset = PlaybackStartSeconds(item, info);
        var end = startAt + Format.SecondsToSamples(RemainingSeconds(info, startOffset));
        var reader = CreateReader(item, info, startAtSeconds: startOffset);
        return new ActiveSource
        {
            Slot = new SourceSlot
            {
                Reader = reader,
                Envelope = EnvelopeFactory.FullLevel(Format, startAt, end),
                StartAtMasterSample = startAt,
                MakeupGainLinear = Makeup(info, settings),
            },
            Item = item,
            Reader = reader,
            EndAtMaster = end,
            ReportAtMaster = reportAt,
        };
    }

    private IPcmSampleReader CreateReader(PlayoutItem item, ItemInfo info, double startAtSeconds)
        => readerFactory.Create(item, Format, startAtSeconds);

    private static double PlaybackStartSeconds(PlayoutItem item, ItemInfo info)
    {
        var duration = Math.Max(0, info.DurationSeconds);
        var resumeOffset = Math.Clamp(double.IsFinite(item.StartOffsetSeconds) ? item.StartOffsetSeconds : 0, 0, duration);
        var leadIn = Math.Clamp(info.Analysis?.LeadingSilenceSeconds ?? 0, 0, duration);
        return Math.Max(resumeOffset, leadIn);
    }

    private static double RemainingSeconds(ItemInfo info, double startOffsetSeconds)
        => Math.Max(0, info.DurationSeconds - startOffsetSeconds);

    private static float Makeup(ItemInfo info, MixerSettings settings)
        => TransitionMath.MakeupGainLinear(
            info.Analysis is { AnalyzerVersion: > 0 } a ? a.IntegratedLufs : null,
            settings.TargetLufs, settings.MaxMakeupGainDb);

    private async Task<ItemInfo> BuildItemInfoAsync(PlayoutItem item, CancellationToken ct)
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

        var duration = analysis is { DurationSeconds: > 0 } ? analysis.DurationSeconds : item.DurationSeconds;
        return new ItemInfo(item.ItemType, analysis, duration, talkativeness);
    }

    private async Task<MixerSettings> LoadSettingsAsync(CancellationToken ct)
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

    private async Task<TopOfHourGuard?> GetTopOfHourGuardAsync(DateTime utcNow, TimeSpan horizon, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
            if (!settings.NewsEnabled && !settings.WeatherEnabled)
            {
                return null;
            }

            var introGrace = TopOfHourScheduler.NormalizeIntroGraceSeconds(settings.TopOfHourIntroGraceSeconds);
            var lateWindow = TopOfHourScheduler.NormalizeLateWindowSeconds(TopOfHourScheduler.DefaultLateWindowSeconds);
            var fadeOut = TopOfHourScheduler.NormalizeFadeOutSeconds(settings.TopOfHourFadeOutSeconds);
            var minTarget = utcNow.AddSeconds(-lateWindow);
            var maxTarget = utcNow
                .Add(horizon < TimeSpan.Zero ? TimeSpan.Zero : horizon)
                .AddSeconds(introGrace);
            var package = await db.NewsPackages.AsNoTracking()
                .Where(package => package.Kind == NewsPackageKind.TopOfHour
                    && package.TargetUtc >= minTarget
                    && package.TargetUtc <= maxTarget
                    && (package.Status == NewsPackageStatus.Pending
                        || package.Status == NewsPackageStatus.Retrying
                        || package.Status == NewsPackageStatus.Ready
                        || package.Status == NewsPackageStatus.Queued))
                .OrderBy(package => package.TargetUtc)
                .FirstOrDefaultAsync(ct);

            return package is null
                ? null
                : new TopOfHourGuard(package.TargetUtc, introGrace, lateWindow, fadeOut, package.Status);
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

    private void MixAndEmit(
        MixerCore core, List<ActiveSource> actives, long masterPos,
        short[] output, float[] accumulator, short[] scratch)
    {
        if (actives.Count == 0)
        {
            Array.Clear(output);
            return;
        }

        _slotView.Clear();
        foreach (var active in actives)
        {
            _slotView.Add(active.Slot);
        }

        core.MixFrame(masterPos, _slotView, output, accumulator, scratch);
    }

    private readonly List<SourceSlot> _slotView = new(4);

    private async Task EmitDueEventsAsync(List<ActiveSource> actives, long masterPos, CancellationToken ct)
    {
        foreach (var active in actives)
        {
            if (!active.Reported && masterPos >= active.ReportAtMaster)
            {
                active.Reported = true;
                stateStore.MarkStarted(active.Item);
                await reporter.ReportStartedAsync(active.Item, ct);
            }
        }
    }

    private async Task FlushDueLogsAsync(MixerCore core, List<PendingLog> logs, long masterPos, CancellationToken ct)
    {
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            var entry = logs[i];
            if (masterPos < entry.CompleteAtMaster)
            {
                continue;
            }

            logs.RemoveAt(i);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                db.TransitionLog.Add(new TransitionLogEntry
                {
                    OccurredAt = DateTime.UtcNow,
                    OutgoingType = entry.Outgoing.ItemType,
                    OutgoingId = entry.Outgoing.ItemId,
                    IncomingType = entry.Incoming.ItemType,
                    IncomingId = entry.Incoming.ItemId,
                    Strategy = entry.Plan.Strategy.ToString(),
                    OverlapSeconds = entry.Plan.OverlapSeconds,
                    GapMs = entry.Plan.GapMs,
                    ParametersJson = JsonSerializer.Serialize(new
                    {
                        reasonTrace = entry.Plan.ReasonTrace,
                        duckLevelDb = entry.Plan.DuckLevelDb,
                        incomingStartOffsetSeconds = entry.Plan.IncomingStartOffsetSeconds,
                        underruns = core.UnderrunCount,
                    }),
                    ClipCount = core.ClipCount - entry.ClipBaseline,
                });
                await db.SaveChangesAsync(ct);
                mixerUpdates.Publish();
                metrics.MixerTransition(entry.Plan.Strategy.ToString(), core.ClipCount - entry.ClipBaseline);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write transition log entry");
            }
        }
    }

    private async Task<PlayoutItem?> TryDequeueAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await queue.DequeueAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task WriteFrameAsync(
        Stream encoderInput, short[] shorts, byte[] bytes, bool clear, CancellationToken ct)
    {
        if (clear)
        {
            Array.Clear(bytes);
        }
        else
        {
            Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        }

        await encoderInput.WriteAsync(bytes, ct);
    }
}

internal static class MixerCoreExtensions
{
    /// <summary>Counters are per-session diagnostics; reset while idle so a long
    /// silence between items doesn't blur transition attribution.</summary>
    public static void ResetCountersIfIdle(this MixerCore core, bool idle)
    {
        if (idle && (core.ClipCount > 0 || core.UnderrunCount > 0))
        {
            core.ResetCounters();
        }
    }

    /// <summary>Dispose a PCM reader only if it owns unmanaged resources; the
    /// in-memory test fake is not IDisposable and must not box/cast-thrown.</summary>
    public static void DisposeIfDisposable(this IPcmSampleReader reader)
    {
        if (reader is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
