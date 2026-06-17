using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
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
    IMixPlanner planner,
    MixerDiagnostics diagnostics,
    FfmpegProcessRegistry ffmpegRegistry,
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<StreamOptions> streamOptions,
    IOptions<RadioOptions> radioOptions,
    ILogger<AudioMixerEngine> logger)
{
    private static readonly PcmFormat Format = new();

    private sealed class ActiveSource
    {
        public required SourceSlot Slot { get; init; }

        public required PlayoutItem Item { get; init; }

        public required FfmpegPcmSampleReader Reader { get; init; }

        public required long EndAtMaster { get; set; }

        public long ReportAtMaster { get; set; }

        public bool Reported { get; set; }
    }

    private sealed record PendingLog(
        PlayoutItem Outgoing, PlayoutItem Incoming, TransitionPlan Plan, int ClipBaseline, long CompleteAtMaster);

    /// <summary>Runs until cancelled, the encoder dies, or the mixer/playout flag
    /// turns the session off (returns at an item boundary).</summary>
    public async Task RunSessionAsync(
        Process encoder, Stream encoderInput,
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

        diagnostics.SessionStarted();
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

                    diagnostics.Update(
                        Format.SamplesToSeconds(masterPos),
                        actives.Select(a => $"{a.Item.Title} [{DescribePhase(a, masterPos)}]"));
                }

                if (actives.Count == 0)
                {
                    if (stopScheduling)
                    {
                        return;
                    }

                    // Nothing playing: pull the next item (or stream silence).
                    var item = await TryDequeueAsync(TimeSpan.FromSeconds(1), ct);
                    if (item is null)
                    {
                        await WriteFrameAsync(encoderInput, outputShorts, outputBytes, clear: true, ct);
                        masterPos += PcmFormat.FrameSamples;
                        continue;
                    }

                    settings = await LoadSettingsAsync(ct);
                    await StartItemChainAsync(item, masterPos, actives, settings, ct);
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
                        var incoming = await TryDequeueAsync(TimeSpan.FromMilliseconds(50), ct);
                        if (incoming is not null)
                        {
                            settings = await LoadSettingsAsync(ct);
                            await ApplyTransitionAsync(current, incoming, actives, settings, core, pendingLogs, ct);
                            transitionPlanned = true;
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

                        a.Reader.Dispose();
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
            foreach (var active in actives)
            {
                active.Reader.Dispose();
            }
        }
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
        actives.Add(CreateSource(item, info, masterPos, settings, EnvelopeKind.Full, reportAt: masterPos));
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
        var songStart = masterPos;
        var talkStart = songStart + Format.SecondsToSamples(talkStartOffset);
        var talkEnd = talkStart + Format.SecondsToSamples(talkInfo.DurationSeconds);
        var songEnd = songStart + Format.SecondsToSamples(songInfo.DurationSeconds);
        var duckReleaseEnd = songStart + Format.SecondsToSamples(introEnd);

        // Song bed: ducked under the talk; release ramp ENDS exactly at IntroEnd.
        var songEnvelope = EnvelopeFactory.DuckedBed(
            Format, songStart, songEnd,
            duckStartSample: songStart,
            duckEndSample: Math.Max(talkEnd, duckReleaseEnd),
            settings.DuckLevelDb, settings.DuckRampMs);
        var songReader = CreateReader(song, songInfo, startAtSeconds: songInfo.Analysis?.LeadingSilenceSeconds ?? 0);
        actives.Add(new ActiveSource
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
        var talkReader = CreateReader(talk, talkInfo, startAtSeconds: 0);
        actives.Add(new ActiveSource
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
        var rate = Format.SampleRate;
        var outgoingEnd = outgoing.EndAtMaster;
        var leadIn = incomingInfo.Analysis?.LeadingSilenceSeconds ?? 0;

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

                incomingEnd = incomingStart + Format.SecondsToSamples(incomingInfo.DurationSeconds - leadIn);
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
                incomingEnd = incomingStart + Format.SecondsToSamples(incomingInfo.DurationSeconds);
                incomingEnvelope = EnvelopeFactory.FullLevel(Format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                pendingLogs.Add(new PendingLog(outgoing.Item, incoming, plan, core.ClipCount, outgoingEnd));
                break;
            }

            default: // HardCut (and IntroTalkOver never reaches here: planned at item start)
            {
                incomingStart = outgoingEnd + Format.SecondsToSamples(plan.GapMs / 1000.0);
                incomingEnd = incomingStart + Format.SecondsToSamples(incomingInfo.DurationSeconds - leadIn);
                incomingEnvelope = EnvelopeFactory.FullLevel(Format, incomingStart, incomingEnd);
                reportAt = incomingStart;
                pendingLogs.Add(new PendingLog(outgoing.Item, incoming, plan, core.ClipCount, incomingStart));
                break;
            }
        }

        var reader = CreateReader(incoming, incomingInfo, startAtSeconds: leadIn);
        actives.Add(new ActiveSource
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

    // --- helpers --------------------------------------------------------------------

    private enum EnvelopeKind
    {
        Full,
    }

    private ActiveSource CreateSource(
        PlayoutItem item, ItemInfo info, long startAt, MixerSettings settings, EnvelopeKind _, long reportAt)
    {
        var leadIn = info.Analysis?.LeadingSilenceSeconds ?? 0;
        var end = startAt + Format.SecondsToSamples(info.DurationSeconds - leadIn);
        var reader = CreateReader(item, info, startAtSeconds: leadIn);
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

    private FfmpegPcmSampleReader CreateReader(PlayoutItem item, ItemInfo info, double startAtSeconds)
    {
        var absolutePath = Path.Combine(radioOptions.Value.DataRoot, item.FilePath);
        return new FfmpegPcmSampleReader(
            streamOptions.Value.FfmpegPath, absolutePath, Format, startAtSeconds, ffmpegRegistry);
    }

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
}
