using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Playout;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The real-time mixer session (Phase 3a): owns the master sample clock, feeds
/// summed PCM into the existing encoder pipe, and realises TransitionPlans as
/// overlapping SourceSlots. Runs INSTEAD of the legacy per-item copy loop while
/// MixerEnabled=true; returns at an item boundary when the flag flips off.
/// The pure math lives in Core (<see cref="SourceScheduler"/>,
/// <see cref="TransitionRealizer"/>, <see cref="FadeRealizer"/>, <see cref="MixerCore"/>);
/// the database surface lives in <see cref="MixerSessionStore"/>.
/// </summary>
public sealed class AudioMixerEngine(
    IPlayoutQueue queue,
    IPlaybackReporter reporter,
    PlayoutStateStore stateStore,
    TrackDeletionService trackDeletions,
    EmergencyFallbackTrackService emergencyFallback,
    IMixPlanner planner,
    MixerDiagnostics diagnostics,
    IMixerUpdatePublisher mixerUpdates,
    TimedPlayoutInterruptService timedInterrupts,
    IPcmSampleReaderFactory readerFactory,
    MixerSessionStore store,
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

    /// <summary>Runs until cancelled, the encoder dies, or the mixer/playout flag
    /// turns the session off (returns at an item boundary).</summary>
    public async Task RunSessionAsync(
        IMixerEncoderSink encoder, Stream encoderInput,
        Func<CancellationToken, Task<bool>> sessionStillWanted, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? offAirRequested = null)
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
        MixerSettings settings = await store.LoadSettingsAsync(ct);
        TopOfHourGuard? topOfHourGuard = await store.GetTopOfHourGuardAsync(DateTime.UtcNow, TimeSpan.Zero, ct);
        PlayoutItem? lastCompletedItem = null;

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
                    if (!stopScheduling && !await sessionStillWanted(ct))
                    {
                        stopScheduling = true;
                        // Off air (operator switched On Air off) ⇒ silence ASAP: fast-fade
                        // everything currently on air — including any crossfade-staged next
                        // track — so no further song slips through. A plain mixer-disable
                        // (still on air) just stops scheduling and lets the current item
                        // finish before handing back to the legacy loop.
                        if (offAirRequested is not null && await offAirRequested(ct))
                        {
                            ApplyOffAirFade(masterPos, actives);
                        }
                    }

                    topOfHourGuard = await store.GetTopOfHourGuardAsync(DateTime.UtcNow, TimeSpan.Zero, ct);
                    PublishLive(masterPos, actives);
                }

                // KNOWN EDGE CASE (accepted, not handled): the news must not start in the
                // gap right after a *song intro* announcement — the introduced song should
                // follow it. The common case is safe: an IntroTalkOver keeps the song's bed
                // active alongside the talk, so actives is never empty between them and this
                // consume can't slot the package in. But a plain song intro that hard-cuts to
                // its song leaves a momentary empty-actives gap; if that gap falls inside the
                // claim window, TryConsume here can fire the package between the intro and its
                // song. Closing it would require threading "this announcement introduces the
                // next track" into the consume decision; deemed too rare to be worth it.
                if (!stopScheduling && timedInterrupts.TryConsume(DateTime.UtcNow) is { } interrupt)
                {
                    settings = await store.LoadSettingsAsync(ct);
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

                    if (queue.PeekNext() is null
                        && await emergencyFallback.TryCreateFallbackTrackAsync(lastCompletedItem, ct) is { } fallback)
                    {
                        queue.Enqueue(fallback);
                    }

                    var item = queue.PeekNext() is null
                        ? null
                        : await TryDequeueAsync(TimeSpan.FromSeconds(1), ct);
                    if (item is null)
                    {
                        await WriteFrameAsync(encoderInput, outputShorts, outputBytes, clear: true, ct);
                        masterPos += PcmFormat.FrameSamples;
                        continue;
                    }

                    settings = await store.LoadSettingsAsync(ct);
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
                            && await store.GetTopOfHourGuardAsync(
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
                                settings = await store.LoadSettingsAsync(ct);
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
                FlushDueLogs(core, pendingLogs, masterPos);

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
                        lastCompletedItem = a.Item;
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
            var talkInfo = await store.BuildItemInfoAsync(item, ct);
            var peeked = queue.PeekNext()!;
            var songInfo = await store.BuildItemInfoAsync(peeked, ct);
            var plan = planner.Plan(talkInfo, songInfo, settings);

            logger.LogInformation(
                "Mixer decision: \"{Out}\" → \"{In}\" | in: {InAnalysis} | {Trace}",
                item.Title, peeked.Title, DescribeAnalysis(songInfo), plan.ReasonTrace);
            diagnostics.DecisionMade($"{item.Title} → {peeked.Title}: {plan.ReasonTrace}");
            mixerUpdates.Publish();

            if (plan.Strategy == MixStrategy.IntroTalkOver)
            {
                if (songInfo.Analysis?.IntroEndSeconds is { } introEnd)
                {
                    var song = await TryDequeueAsync(TimeSpan.FromMilliseconds(50), ct);
                    if (song is not null)
                    {
                        var (scheduledSong, scheduledTalk) = SourceScheduler.PlanIntroTalkOver(
                            item, talkInfo, song, songInfo, introEnd, plan, masterPos, settings, Format);
                        AddActiveSource(actives, CreateActiveSource(song, scheduledSong));
                        AddActiveSource(actives, CreateActiveSource(item, scheduledTalk));
                        logger.LogInformation(
                            "Mixer: IntroTalkOver — \"{Talk}\" over the intro of \"{Song}\" (post at {Intro:F1}s). {Trace}",
                            item.Title, song.Title, introEnd, plan.ReasonTrace);
                        return;
                    }
                }
                else
                {
                    logger.LogWarning(
                        "IntroTalkOver planned for \"{Talk}\" over \"{Song}\" but IntroEnd is missing; playing the talk in full instead",
                        item.Title, peeked.Title);
                }
            }
        }

        var info = await store.BuildItemInfoAsync(item, ct);
        AddActiveSource(actives, CreateActiveSource(
            item, SourceScheduler.PlanFullLevel(item, info, settings, Format, masterPos, reportAt: masterPos)));
        logger.LogInformation(
            "Mixer: \"{Title}\" starts ({Duration:F0}s, {Analysis}, makeup {Makeup:F1} dB)",
            item.Title, info.DurationSeconds, DescribeAnalysis(info),
            20 * Math.Log10(SourceScheduler.Makeup(info, settings)));
    }

    // --- transitions ---------------------------------------------------------------

    private async Task ApplyTransitionAsync(
        ActiveSource outgoing, PlayoutItem incoming, List<ActiveSource> actives,
        MixerSettings settings, MixerCore core, List<PendingLog> pendingLogs, CancellationToken ct)
    {
        var outgoingInfo = await store.BuildItemInfoAsync(outgoing.Item, ct);
        var incomingInfo = await store.BuildItemInfoAsync(incoming, ct);
        var plan = planner.Plan(outgoingInfo, incomingInfo, settings);

        logger.LogInformation(
            "Mixer decision: \"{Out}\" → \"{In}\" | out: {OutAnalysis} | in: {InAnalysis} | {Trace}",
            outgoing.Item.Title, incoming.Title,
            DescribeAnalysis(outgoingInfo), DescribeAnalysis(incomingInfo), plan.ReasonTrace);
        diagnostics.DecisionMade($"{outgoing.Item.Title} → {incoming.Title}: {plan.ReasonTrace}");
        mixerUpdates.Publish();

        var realization = TransitionRealizer.Realize(
            plan, outgoingInfo, outgoing.Slot.Envelope, outgoing.EndAtMaster,
            incoming, incomingInfo, settings, Format);

        switch (realization.Fallback)
        {
            case TransitionFallback.BeatDataMissing:
                logger.LogWarning(
                    "BeatAlignedFade planned for \"{Out}\" → \"{In}\" but beat data is missing or invalid; using a plain crossfade",
                    outgoing.Item.Title, incoming.Title);
                break;
            case TransitionFallback.OutroDataMissing:
                logger.LogWarning(
                    "OutroTalkOver planned for \"{Out}\" → \"{In}\" but OutroStart is missing; using a hard cut",
                    outgoing.Item.Title, incoming.Title);
                break;
        }

        if (realization.OutgoingEndAtMaster is { } newOutgoingEnd)
        {
            outgoing.EndAtMaster = newOutgoingEnd;
        }

        pendingLogs.Add(new PendingLog(
            outgoing.Item, incoming, plan, core.ClipCount, realization.LogCompleteAtMaster));
        AddActiveSource(actives, CreateActiveSource(incoming, realization.Incoming));

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
                    active.EndAtMaster = FadeRealizer.FadeToSilence(
                        active.Slot.Envelope, active.EndAtMaster, fadeStart, fadeEnd);
                }

                startAt = fadeEnd;
            }
        }

        var info = await store.BuildItemInfoAsync(interrupt.Item, ct);
        AddActiveSource(actives, CreateActiveSource(
            interrupt.Item,
            SourceScheduler.PlanFullLevel(interrupt.Item, info, settings, Format, startAt, reportAt: startAt)));
        logger.LogInformation(
            "Mixer timed package: starting {Title} at {Delay:F1}s after decision",
            interrupt.Item.Title,
            Format.SamplesToSeconds(startAt - masterPos));
    }

    // Fast declick fade applied when the operator switches On Air off mid-session.
    // Short enough to feel immediate, long enough to avoid a hard click.
    private const double OffAirFadeSeconds = 1.5;

    /// <summary>
    /// Ramps every active source (current item AND any crossfade-staged incoming)
    /// down to silence over <see cref="OffAirFadeSeconds"/>, then trims its end so
    /// the cleanup loop drops it. Guarantees the mount falls silent within ~1.5 s of
    /// going off air instead of letting a pre-staged next track play out.
    /// </summary>
    private void ApplyOffAirFade(long masterPos, List<ActiveSource> actives)
    {
        var fadeEnd = masterPos + Math.Max(1, Format.SecondsToSamples(OffAirFadeSeconds));
        foreach (var active in actives.Where(source => source.EndAtMaster > masterPos))
        {
            active.EndAtMaster = FadeRealizer.FadeToSilence(
                active.Slot.Envelope, active.EndAtMaster, masterPos, fadeEnd);
        }

        logger.LogInformation("Mixer off air — fading {Count} active source(s) to silence over {Fade:F1}s", actives.Count, OffAirFadeSeconds);
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

        // The hold-fade clears MUSIC off the air at the boundary so the scheduled
        // package can start cleanly. It must NEVER fade the package announcement
        // itself: once the timed interrupt has put the package composite on air,
        // the guard is still active for the ~2 s until the dispatcher flips the
        // package to Played, and fading everything here silenced the top-of-hour
        // package ~2 s in and handed straight back to a song.
        var tracks = actives
            .Where(source => source.Item.ItemType == PlayoutItemType.Track
                && source.EndAtMaster > masterPos)
            .ToList();
        if (tracks.Count == 0)
        {
            return false;
        }

        var naturalEnd = tracks
            .Select(source => source.EndAtMaster)
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
        foreach (var active in tracks)
        {
            active.EndAtMaster = FadeRealizer.FadeToSilence(
                active.Slot.Envelope, active.EndAtMaster, masterPos, fadeEnd);
        }

        logger.LogInformation(
            "Mixer top-of-hour hold: fading current audio because package {Status} for {Target:u} is due",
            guard.Status,
            guard.TargetUtc);
        return true;
    }

    // --- helpers --------------------------------------------------------------------

    private void AddActiveSource(List<ActiveSource> actives, ActiveSource source)
    {
        actives.Add(source);
        trackDeletions.MarkPlaybackStarted(source.Item);
    }

    /// <summary>Attaches the PCM reader to a scheduled source and wraps both for the mix loop.</summary>
    private ActiveSource CreateActiveSource(PlayoutItem item, ScheduledSource scheduled)
    {
        var reader = readerFactory.Create(item, Format, scheduled.SourceStartSeconds);
        return new ActiveSource
        {
            Slot = new SourceSlot
            {
                Reader = reader,
                Envelope = scheduled.Envelope,
                StartAtMasterSample = scheduled.StartAtMaster,
                MakeupGainLinear = scheduled.MakeupGainLinear,
            },
            Item = item,
            Reader = reader,
            EndAtMaster = scheduled.EndAtMaster,
            ReportAtMaster = scheduled.ReportAtMaster,
        };
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

    // Runs on the frame-cadence master-clock thread: only snapshot the counters
    // here and hand the DB write to a background task so a slow query at a
    // transition point cannot stall the mix clock.
    private void FlushDueLogs(MixerCore core, List<PendingLog> logs, long masterPos)
    {
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            var entry = logs[i];
            if (masterPos < entry.CompleteAtMaster)
            {
                continue;
            }

            logs.RemoveAt(i);
            store.WriteTransitionLogAsync(
                entry.Outgoing, entry.Incoming, entry.Plan, core.ClipCount - entry.ClipBaseline, core.UnderrunCount)
                .Forget();
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
