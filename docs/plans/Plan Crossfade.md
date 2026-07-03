# WhipRadio — Phase 3a Plan: Audio Mixer & Intelligent Transitions

> **Audience:** AI coding agent (Claude Fable 5 / Copilot).
> Phase 1, 1.5 and 2 are running. Phase 3a introduces a real-time **AudioMixerService**
> sitting between media sources and the Icecast encoder, plus **per-track audio analysis**
> (BPM, beat grid, intro/outro, loudness) and a **MixPlanner** that picks a transition
> strategy per item pair. Execute milestones in order; `dotnet build` + `dotnet test`
> stay green throughout.
>
> **Prime rule (unchanged): never break the running stream.** The mixer ships behind a
> feature flag `Mixer:Enabled` (default **false** until M5). The legacy sequential
> playout path from Phase 1 remains intact and selectable until Phase 3b removes it.

---

## 1. Goal & Scope

### In scope
- **AudioMixerService**: sample-accurate PCM mixing of overlapping sources in C# (no
  per-transition ffmpeg filtergraphs). Decoders stay ffmpeg; encoder stays the existing
  long-lived ffmpeg → Icecast process.
- **Audio analysis** (extension of the music sidecar): BPM + confidence, beat grid,
  intro-end / outro-start detection, leading/trailing silence, integrated loudness
  (EBU R128 LUFS), downsampled energy profile. Stored per item in the DB.
- **MixPlanner**: pure, unit-testable decision component producing a `TransitionPlan`
  for every adjacent item pair. Strategy set below. **`HardCut` is a first-class,
  routinely chosen strategy** — the normal case after announcements and between two
  announcements, and a legitimate random choice between two songs. It is *not* an
  error fallback (though it also serves as the safe degradation when analysis is
  missing).
- **Loudness normalization**: every item is gain-staged to a target loudness
  (default −16 LUFS integrated, clamp ±6 dB makeup) so ducking and crossfades behave
  predictably across TTS engines and music backends.
- **"Hit the post"**: announcements can be laid over a song intro such that the talk
  ends just before the energy kicks in.
- Admin panel section, stats counters, transition log.

### Out of scope (Phase 3b+)
- Tempo stretching / time-warping for beat matching (we only beat-*align*, no stretch)
- Top-of-the-hour precision scheduling
- Jingle library (note: the mixer's source model makes jingles a trivial add later)
- Podcasts, host duets, news/traffic

### Context notes (informational, no action)
- Primary music backend is now **ACE-Step**; MusicGen remains available. A
  human-curated music library (`Track.Backend = "library"`) is planned. **Analysis is
  source-agnostic by design** — it operates on WAV files only, so generated and
  imported tracks flow through identical analysis and mixing paths. ACE-Step's
  explicit song-structure metadata MAY later feed `IntroEndSeconds` directly
  (optional improvement, not in this phase).

---

## 2. Architecture

```
                       ┌──────────────────────────────────────────────┐
                       │            AudioMixerService                 │
 Track WAV ──decoder──►│  SourceSlot A ─┐                             │
 (ffmpeg s16le pipe)   │                ├─► MixerCore ─► master clamp │──► encoder ffmpeg ──► Icecast
 Announce WAV ─decoder►│  SourceSlot B ─┘   (sum + gain envelopes)    │      (existing)
                       │        ▲                                     │
                       │        │ sample-clock scheduler              │
                       └────────┼─────────────────────────────────────┘
                                │
                        TransitionPlan (per pair)
                                ▲
                          MixPlanner (pure)
                                ▲
                 MediaAnalysis (DB)  +  MixerSettings  +  item kinds
```

- **Canonical PCM format everywhere:** 44 100 Hz, stereo, interleaved signed 16-bit
  little-endian (`s16le`) — identical to the Phase 1 pipe format. Frame size:
  **1 024 samples/channel (~23.2 ms)**; all scheduling happens in sample time
  (`long samplePos`), never wall-clock.
- **Two source slots are sufficient** for Phase 3a (one outgoing, one incoming).
  Model it as `IReadOnlyList<SourceSlot>` anyway — jingles/beds later become a third
  slot with zero redesign.
- **Naming change vs. earlier discussion:** the per-item analysis record is
  `MediaAnalysis` (stored); the per-transition decision is `TransitionPlan`
  (ephemeral, logged). The earlier working name "MixPlan" conflated both — do not
  introduce a type of that name.

---

## 3. Data Model (migration `Phase3a`)

### MediaAnalysis *(new table — one row per analysed media item)*
| Field | Type | Notes |
|---|---|---|
| Id | Guid PK | |
| ItemType | enum: Track / Announcement | |
| ItemId | Guid | FK by convention (no hard FK across two tables) |
| Bpm | double? | null when undetectable |
| BpmConfidence | double | 0–1 (librosa tempo strength heuristic) |
| BeatGridJson | string? | JSON `double[]` of beat timestamps in seconds; null for announcements |
| IntroEndSeconds | double? | energy onset point; null if low confidence |
| IntroConfidence | double | 0–1 |
| OutroStartSeconds | double? | sustained energy drop point |
| OutroConfidence | double | 0–1 |
| LeadingSilenceSeconds | double | ≥ 0 |
| TrailingSilenceSeconds | double | ≥ 0 |
| IntegratedLufs | double | EBU R128 integrated loudness |
| TruePeakDb | double | |
| EnergyProfileJson | string | JSON `double[]` RMS at 2 Hz, normalised 0–1 |
| DurationSeconds | double | authoritative (replaces ffprobe-only value) |
| AnalyzerVersion | int | bump when algorithm changes → backfill re-runs |
| AnalyzedAt | DateTime | |

Unique index on `(ItemType, ItemId)`.

### TransitionLogEntry *(new table — observability)*
`Id, OccurredAt, OutgoingType, OutgoingId, IncomingType, IncomingId, Strategy (string),
OverlapSeconds (double), GapMs (int), ParametersJson (string), ClipCount (int)`

### StationSettings — add (all hot-reloadable; mixer reads per transition)
| Field | Default | Meaning |
|---|---|---|
| MixerEnabled | false | master flag (flipped to true in M5) |
| TargetLufs | −16.0 | loudness normalization target |
| MaxMakeupGainDb | 6.0 | clamp for quiet items |
| DuckLevelDb | −12.0 | song level under talk |
| DuckRampMs | 800 | duck attack/release |
| DefaultCrossfadeSeconds | 5.0 | EnergyFade overlap |
| BeatAlignBpmTolerancePct | 5.0 | max ΔBPM for BeatAlignedFade |
| HardCutGapAfterTalkMsMin / Max | 200 / 600 | sampled uniformly |
| HardCutGapSongMsMin / Max | 0 / 150 | sampled uniformly |
| PostHitSafetyMs | 800 | talk must end this long before IntroEnd |
| StrategyWeightsJson | see §6 | per pair-kind weight table |
| AnalysisRequired | false | if true, unanalysed items are skipped by selector |

---

## 4. Analysis Pipeline

### 4.1 Sidecar endpoint (extend `sidecars/music`)
New deps: `librosa`, `pyloudnorm`, `soundfile` (torch already present).
Mount the existing data volume **read-only** into the music sidecar (`/data`),
env `DATA_ROOT=/data`.

```
POST /analyze
  body: { "path": "library/tracks/{id}.wav" }      # relative to DATA_ROOT
  resp: {
    "bpm": 128.1, "bpm_confidence": 0.87,
    "beats": [0.42, 0.89, 1.36, ...],              # seconds; omit for speech
    "intro_end_seconds": 14.2,  "intro_confidence": 0.74,
    "outro_start_seconds": 221.5, "outro_confidence": 0.61,
    "leading_silence_seconds": 0.08,
    "trailing_silence_seconds": 1.94,
    "integrated_lufs": -19.3, "true_peak_db": -1.2,
    "energy_profile": [0.02, 0.05, 0.31, ...],     # 2 Hz, 0..1
    "duration_seconds": 244.6,
    "analyzer_version": 1,
    "mode": "music" | "speech"
  }
```
- `mode=speech` (announcements): skip beats/intro/outro; return loudness, silence,
  duration, energy only. The orchestrator passes `"mode"` explicitly based on ItemType.
- **Algorithms** (document in sidecar README):
  - BPM/beats: `librosa.beat.beat_track` on percussive component
    (`librosa.effects.hpss`). Confidence: normalised tempogram peak ratio.
  - Intro end: first time the 2 Hz RMS curve stays ≥ 55 % of track median RMS for
    ≥ 2 consecutive seconds, cross-checked with `librosa.onset.onset_strength`
    cumulative jump. Confidence from agreement of the two detectors.
  - Outro start: last time RMS falls below 45 % of median and stays there ≥ 3 s.
  - Silence: `librosa.effects.trim` boundaries at top_db=40.
  - LUFS: `pyloudnorm.Meter(...).integrated_loudness`; TruePeak via 4× oversampled max.
- Analysis of a 7-min WAV must complete < 20 s CPU-only (assert in sidecar test).

### 4.2 Orchestrator integration
- `IAudioAnalysisClient` (Infrastructure): `Task<MediaAnalysisDto> AnalyzeAsync(string relativePath, AnalysisMode mode, CancellationToken ct)` — 60 s timeout.
- **Production-time hook:** `MusicProductionService` and `AnnouncementProductionService`
  call analysis immediately after writing the WAV; persist `MediaAnalysis` row in the
  same unit of work as the Track/Announcement row. On analysis failure: log warning,
  store row with nulls + `AnalyzerVersion=0` (item remains playable; planner degrades).
- **`AnalysisBackfillService`** (BackgroundService): every 10 min, query up to 5 items
  lacking a current-version `MediaAnalysis` row (legacy tracks, failed attempts,
  version bumps), analyse serially. Pauses while a music generation job is in flight
  (single shared semaphore with MusicProductionService — GPU/CPU is contended).
- Announcement analysis only needs `mode=speech` (fast, < 2 s).

---

## 5. MixerCore — pure DSP (C#, in `LlamaRadio.Core/Audio`)

All of this is plain math on `short[]` / `float[]` buffers. No I/O, no processes —
100 % unit-testable.

### 5.1 Types
```csharp
public sealed record PcmFormat(int SampleRate = 44100, int Channels = 2); // s16le interleaved

public sealed class GainEnvelope
{
    // Breakpoints in absolute sample positions of the MASTER clock.
    // Interpolation between breakpoints: per segment, Linear or EqualPower.
    public void AddBreakpoint(long samplePos, float gain, RampShape shapeToNext);
    public float GainAt(long samplePos);            // O(log n) lookup
}

public enum RampShape { Hold, Linear, EqualPowerIn, EqualPowerOut }

public sealed class SourceSlot
{
    public required IPcmSampleReader Reader { get; init; }   // pull model
    public required GainEnvelope Envelope { get; init; }
    public required long StartAtMasterSample { get; init; }  // when this source begins
    public long SourceOffsetSamples { get; init; }           // skip into the file (silence trim)
    public float MakeupGainLinear { get; init; } = 1f;       // loudness normalization
    public bool Finished { get; private set; }
}

public interface IPcmSampleReader  // implemented over decoder stdout w/ ring buffer
{
    /// Fills frame with up to count interleaved samples; returns samples written (0 = EOF).
    int Read(Span<short> frame);
}
```

### 5.2 Mixing rules (implement exactly; each rule gets tests)
1. **Equal-power crossfade:** for fade progress `x ∈ [0,1]`:
   `gainOut = cos(x·π/2)`, `gainIn = sin(x·π/2)` → `gainOut² + gainIn² = 1`.
2. **Anti-click micro-ramps:** EVERY source start/stop gets an implicit 15 ms linear
   ramp from/to 0, regardless of strategy (including HardCut). This is click
   protection, not a "fade", and is invisible to the strategy layer.
3. **Summation & headroom:** `sum = Σ(sample_i × envGain_i × makeup_i)` in `float`.
   Master stage: hard clamp to [−32768, 32767] with a `ClipCounter` (per transition,
   logged to `TransitionLogEntry.ClipCount`). No tanh/limiter in 3a — loudness
   normalization to −16 LUFS leaves ~6 dB headroom; clipping should be ~0 and the
   counter proves it.
4. **Makeup gain:** `makeupDb = clamp(TargetLufs − IntegratedLufs, −MaxMakeupGainDb, +MaxMakeupGainDb)`;
   linear = `10^(dB/20)`. Items without analysis get makeup 1.0.
5. **Underrun:** if a reader returns fewer samples than requested mid-stream, zero-fill
   the gap, increment an `UnderrunCounter`, log warning. Never stall the master clock.

### 5.3 Scheduling math (pure helpers in `TransitionMath`, all unit-tested)
- **Hit the post** (talk over incoming song intro):
  `talkStartInSong = max(0, IntroEnd − talkDuration − PostHitSafetyMs/1000)`
  Song starts at master sample S (ducked); announcement source starts at
  `S + talkStartInSong·rate`. Duck release ramp (DuckRampMs) is scheduled to END
  exactly at `S + IntroEnd·rate`. Precondition checked by planner:
  `IntroEnd ≥ talkDuration·0.5` (else strategy not eligible).
- **Beat alignment** (no stretching): given outgoing beat grid `B_out`, incoming grid
  `B_in`, desired overlap start ~`anchor` (OutroStart if confident, else
  `duration − DefaultCrossfadeSeconds`):
  1. `b_out = nearest beat in B_out to anchor`
  2. incoming's first audible beat `b_in0 = B_in[0] (after leading-silence trim)`
  3. incoming `StartAtMasterSample = masterSampleOf(b_out) − b_in0·rate`
  4. crossfade window starts at `b_out`, duration = `n` outgoing beats where
     `n = round(DefaultCrossfadeSeconds × Bpm_out / 60)`, min 4, max 16 beats.
- **Gap sampling (HardCut):** gap drawn uniformly from the configured min/max for the
  pair kind; gap is rendered as scheduled silence on the master clock.

---

## 6. MixPlanner — strategy decision (pure, in `LlamaRadio.Core/Audio`)

```csharp
public enum MixStrategy
{
    HardCut,          // first-class default for talk; legitimate everywhere
    EnergyFade,       // equal-power crossfade, anchor = end − duration
    OutroBridgeIn,    // equal-power crossfade anchored at outgoing OutroStart
    BeatAlignedFade,  // crossfade with beat-grid alignment (§5.3)
    IntroTalkOver,    // talk over incoming song intro ("hit the post")
    OutroTalkOver     // talk starts over outgoing song outro (ducked), song ends under talk
}

public sealed record TransitionPlan(
    MixStrategy Strategy,
    double OverlapSeconds,        // 0 for HardCut
    int GapMs,                    // >0 only for HardCut
    double? IncomingStartOffsetSeconds,  // talk-over scheduling / silence trim
    double DuckLevelDb,
    string ReasonTrace);          // human-readable decision trace for the log
```

### 6.1 Decision procedure
`MixPlanner.Plan(outgoing: ItemInfo, incoming: ItemInfo, settings) → TransitionPlan`
where `ItemInfo = (ItemType, MediaAnalysis?, DurationSeconds, Kind)`.

1. Determine **pair kind**: `TalkToTalk`, `TalkToSong`, `SongToTalk`, `SongToSong`.
2. Build the **eligible set** with hard preconditions:

| Strategy | Eligible when |
|---|---|
| HardCut | always |
| EnergyFade | pair = SongToSong AND both durations > 2× DefaultCrossfadeSeconds |
| OutroBridgeIn | SongToSong AND outgoing `OutroConfidence ≥ 0.5` |
| BeatAlignedFade | SongToSong AND both `BpmConfidence ≥ 0.6` AND ΔBPM ≤ tolerance AND both beat grids present |
| IntroTalkOver | TalkToSong AND incoming `IntroConfidence ≥ 0.5` AND `IntroEnd ≥ talkDuration·0.5` |
| OutroTalkOver | SongToTalk AND outgoing `OutroConfidence ≥ 0.5` |

3. **Weighted random pick** over the eligible set using `StrategyWeightsJson`
   (defaults below). Randomness is deliberate — real radio varies its transitions.
   Seedable `IRandomSource` injected for deterministic tests.

Default weights:
```json
{
  "TalkToTalk":  { "HardCut": 100 },
  "TalkToSong":  { "HardCut": 55, "IntroTalkOver": 45 },
  "SongToTalk":  { "HardCut": 70, "OutroTalkOver": 30 },
  "SongToSong":  { "HardCut": 20, "EnergyFade": 25, "OutroBridgeIn": 25, "BeatAlignedFade": 30 }
}
```
4. Fill parameters (overlap, gap, offsets) via `TransitionMath`; write a
   `ReasonTrace` like
   `"SongToSong; eligible=[HardCut,EnergyFade,BeatAlignedFade]; ΔBPM=2.1%; picked=BeatAlignedFade(w=30)"`.
5. **Degradation is built in:** missing/low-confidence analysis simply shrinks the
   eligible set — worst case the set is `{HardCut}`, which is a perfectly good radio
   transition, not an error. Never throw for missing analysis.

---

## 7. AudioMixerService — runtime (Orchestrator)

Replaces the per-item copy loop of `PlayoutService` **when `MixerEnabled=true`**;
the encoder process management, restart logic, and Icecast metadata calls stay where
they are.

### 7.1 Responsibilities
1. Own the **master sample clock** (`long _masterPos`), advancing by one frame
   (1 024 samples) per loop iteration, paced by the encoder pipe's backpressure
   (blocking write — the encoder consumes in real time; do NOT add wall-clock sleeps).
2. **Lookahead:** require the playout queue to expose current + next item
   (`IPlayoutQueue.PeekNextAsync`). When the current item's remaining time ≤
   `max(transition window) + 10 s`, request the `TransitionPlan` and **pre-spawn** the
   incoming decoder (prefetch eliminates process-start latency).
3. Translate the plan into `SourceSlot`s + `GainEnvelope` breakpoints (one method per
   strategy; ~30 lines each; all delegate math to `TransitionMath`).
4. Emit events at the right moments:
   - `ItemAudibleStarted` (PlayLog write + `Track.PlayCount++`): at source start
     for HardCut/talk-over; at **crossfade midpoint** for the three fade strategies.
   - `NowPlayingChanged` (SignalR + Icecast metadata): same moments.
5. Write each completed transition to `TransitionLogEntry` incl. `ReasonTrace` and
   `ClipCount`.
6. **Settings hot-reload:** snapshot `MixerSettings` once per transition (no mid-fade
   changes).

### 7.2 Failure containment
- Decoder process dies mid-item → treat as EOF, plan next transition normally,
  log warning. Stream keeps running.
- Encoder dies → existing restart logic; mixer pauses master clock until pipe is back.
- Analysis client down → items enter with null analysis; planner degrades (see §6).
- `MixerEnabled` flipped off at runtime → finish current transition, then revert to
  legacy path on next item boundary (both paths share the encoder).

---

## 8. Milestones

### M0 — Scaffolding & flag
1. Migration `Phase3a` (§3). Seed `StationSettings` defaults; `MixerEnabled=false`.
2. Interfaces: `IAudioAnalysisClient`, `IPcmSampleReader`, `IMixPlanner`,
   `ITransitionLog`. Empty `LlamaRadio.Core/Audio` namespace with types from §5/§6
   compiling (bodies `NotImplementedException` where needed).
3. `IPlayoutQueue.PeekNextAsync` added; legacy playout untouched.
**Accept:** build green; migration applies; flag visible in settings (read-only UI ok).

### M1 — Analysis sidecar + clients + backfill
1. Implement `/analyze` (§4.1) with the documented algorithms; sidecar unit tests with
   3 committed fixture WAVs (generated synthetically in a fixture script: a 128 BPM
   click-track with 10 s quiet intro; a constant-energy tone; a speech-like noise burst).
   Assert: BPM within ±2; intro_end within ±1.5 s; LUFS within ±1.
2. Mount data volume read-only into music sidecar (AppHost change).
3. `HttpAudioAnalysisClient` + DTO parsing tests (fake handler).
4. Production-time hooks + `AnalysisBackfillService` + shared generation semaphore.
**Accept:** new track gets a `MediaAnalysis` row automatically; backfill analyses one
legacy track per run; sidecar tests green; 7-min WAV analysis < 20 s.

### M2 — MixerCore DSP
1. Implement `GainEnvelope`, `SourceSlot`, equal-power math, anti-click ramps,
   summation/clamp/clip-counter, makeup gain, underrun zero-fill.
2. `TransitionMath`: hit-the-post, beat alignment, gap sampling, beat-window sizing.
3. **Tests (≥ 25):** envelope interpolation incl. shapes; equal-power identity
   (`g²+g²=1` over the curve at 100 points); anti-click ramp present on HardCut;
   summation clamps and counts clips; makeup clamp ±6 dB; hit-the-post: cases
   (intro long/short/exact, clamp at 0); beat alignment picks nearest beat & offset
   formula; deterministic with seeded random. Golden test: mix two synthetic sine
   sources with a 2 s equal-power fade, assert per-100 ms RMS envelope within ±5 %
   of analytic expectation.
**Accept:** all DSP tests green; zero allocations per frame in the hot loop
(verify with a simple `GC.GetAllocatedBytesForCurrentThread` guard test).

### M3 — AudioMixerService runtime
1. `FfmpegPcmSampleReader : IPcmSampleReader` — decoder process + ring buffer
   (1 s capacity), honoring `SourceOffsetSamples` via ffmpeg `-ss` (input-seek).
2. Mixer loop (§7.1), slot lifecycle, prefetch, event emission, transition logging.
3. Wire behind `MixerEnabled`; legacy path verified still default.
4. Integration test (Linux CI-safe): pipe two generated 5 s WAVs through the mixer
   into a *file* sink (encoder stub), assert output duration = itemA + itemB − overlap
   ± 1 frame, and assert the PlayLog/NowPlaying event order.
**Accept:** with flag ON locally: station streams, transitions audible, no underruns
in a 15-min run; flag OFF: byte-identical legacy behavior.

### M4 — MixPlanner
1. Implement eligibility table, weighted pick with injected `IRandomSource`,
   parameter fill, `ReasonTrace`.
2. **Tests (≥ 15):** every eligibility rule; degradation to `{HardCut}` with null
   analysis; weight table respected over 10 000 seeded draws (χ² sanity ±3 %);
   ΔBPM boundary at tolerance; IntroTalkOver precondition math; TalkToTalk always
   HardCut with gap in configured range.
**Accept:** planner tests green; ReasonTrace human-readable in transition log.

### M5 — Integration, admin & rollout
1. ShowRunner: ensure announcement production completes early enough for lookahead
   (produce-ahead margin = 30 s; reuse existing pool logic).
2. Admin page **Mixer** section: enable toggle, TargetLufs, DuckLevelDb,
   DefaultCrossfadeSeconds, gap ranges, weights JSON editor (validated), "Re-run
   backfill" button, live counters (transitions by strategy, clips, underruns),
   last-20 transition log with ReasonTrace.
3. `/stats`: transitions-by-strategy chart; clip/underrun totals.
4. **Soak:** 60-min run with ≥ 12 transitions covering ≥ 4 strategies (force via
   temporary weight tweak), zero IO-abort errors, ClipCount total < 10, underruns = 0.
5. Flip default `MixerEnabled=true` (seed + appsettings). Legacy path stays selectable.
**Accept:** DoD below.

---

## 9. Definition of Done — Phase 3a
- [ ] `dotnet build` + `dotnet test` green (≥ 40 new tests across M1–M4)
- [ ] New tracks & announcements get `MediaAnalysis` automatically; backfill covers legacy
- [ ] Loudness-normalised output: talk and music subjectively level (−16 LUFS target)
- [ ] HardCut occurs routinely after announcements (gap 200–600 ms) — verified in transition log
- [ ] IntroTalkOver demonstrably "hits the post" on at least one real track (listen + log)
- [ ] BeatAlignedFade chosen for two close-BPM electronic tracks; audibly on-beat
- [ ] Missing analysis degrades gracefully to HardCut (delete an analysis row to prove)
- [ ] Flag OFF restores Phase-1 sequential playout unchanged
- [ ] Transition log + admin mixer panel + stats counters live
- [ ] 60-min soak: 0 underruns, ClipCount < 10, no errors

## 10. Risks & Fallbacks (apply without asking)
| Risk | Fallback |
|---|---|
| Intro/outro detection unreliable on some genres | Confidence gates already exclude weak detections; strategy set shrinks toward HardCut/EnergyFade — acceptable by design |
| librosa BPM octave errors (64 vs 128) | If `Bpm < 70`, also test `2×Bpm` against tempogram; pick stronger peak; tests cover one synthetic case |
| ffmpeg `-ss` input-seek imprecision | Seek 0.5 s early, discard samples to exact offset in the reader |
| Encoder backpressure stalls (network hiccup to Icecast) | Existing restart logic; mixer master clock pauses with the pipe; ring buffers absorb ≤ 1 s |
| GPU/CPU contention: analysis vs generation | Shared semaphore (M1) serialises; backfill yields to generation |
| Weights JSON edited invalidly in admin | Server-side schema validation; reject save with message; keep last valid |