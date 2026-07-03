# WhipRadio — Phase 3d Brief: DJ Hosts & Cue-Based Mixing

> Design brief. This phase turns a host into a real **DJ host**: a special kind of
> moderator whose shows are always mixed as DJ sets. It does **not** introduce stems,
> model training, or opaque audio generation. The DJ thinks like a host; the mixer executes
> deterministic, cue-based transitions.
>
> Depends on Phase 3a's `MixerCore`, `AudioMixerEngine`, per-track analysis, and
> `MixPlanner`. It extends those concepts rather than replacing them.

---

## 1. Goal

Add a **DJ host** as a first-class host type. A DJ host can run a `DJ Set` format where
track selection, cue points, phrase alignment, EQ moves, tempo matching, and transition
style are planned as a coherent set.

Normal hosts keep using ordinary radio transitions: hard cuts after talk, simple fades,
energy fades, or whatever Phase 3a selects. A DJ set is different: while a DJ host is on
a DJ-format slot, every music-to-music transition is planned through the DJ mixing path.

**Core idea:**

```text
DJ host / LLM
  chooses vibe, arc, track intent, and transition style

DJ MixPlanner
  turns that intent into validated cue-based transition plans

DeckMixerEngine / MixerCore
  executes gain, EQ, filter, and tempo automation sample-accurately
```

The LLM does not mix audio directly. It describes intent and makes high-level music
choices. The deterministic planner and DSP engine own timing, safety, and audio output.

---

## 2. Firm decisions

### 2.1 DJ is a host kind, not a separate actor system

Introduce a host classification, for example:

```csharp
public enum ModeratorKind
{
    Regular,
    DJ,
    WeatherSpecialist,
    NewsSpecialist,
    PodcastGuest,
    ArtistGuest
}
```

Names are open, but the model should express the concept clearly: a DJ is a moderator
with a specialized playout/mixing profile.

A DJ host still has persona, voice, memory, mood, language, and `PromptContext`. The new
part is a `DjProfile` that controls how music is selected and mixed.

### 2.2 Normal hosts do not become DJs accidentally

Regular formats keep the ordinary Phase 3a transition path:

- `HardCut`
- `EnergyFade`
- `OutroBridgeIn`
- `BeatAlignedFade` where safe
- `IntroTalkOver` / `OutroTalkOver`

A DJ set format opts into DJ mixing explicitly. The system must never silently apply
aggressive DJ automation to a normal host's show.

### 2.3 A DJ set always uses DJ mixing semantics

Inside a DJ set, music-to-music transitions are always planned as DJ transitions.

That does not mean every transition must be complex. A low-confidence pair may fall back
to a safe DJ transition such as a phrase-aligned fade or clean phrase cut. It should not
fall back to the ordinary host path unless the stream is in emergency recovery.

### 2.4 Cue points are first-class

Cue points are not just analysis details. They are editable station data.

The DJ system should support both:

- automatically detected cue points from the analysis sidecar;
- operator-approved or manually adjusted cue points from the Web UI.

A manual cue point always outranks an automatic cue point of the same kind.

### 2.5 No stems

Stem separation is explicitly out of scope and not on this roadmap.

This phase must work with ordinary stereo masters only. DJ-style movement comes from:

- gain automation;
- 3-band EQ automation;
- optional high-pass / low-pass filters;
- cue points;
- beat and phrase alignment;
- bounded tempo matching.

No drums/bass/vocal separation is planned.

### 2.6 No ML training / no external DJ-set imitation

Do not train a model from existing DJ mixes in this phase.

Reasons:

- commercial use is being kept open;
- rights and provenance around external DJ mixes are unclear;
- transition quality can get far enough with deterministic analysis, templates, cue
  points, and operator feedback;
- WhipRadio should avoid adding a black-box learning subsystem to the live audio path.

Allowed feedback in this phase:

- store operator ratings;
- store rejected/approved transition plans;
- adjust deterministic weights and presets;
- prefer previously successful strategies for similar track pairs through transparent
  rules.

Not allowed in this phase:

- training a neural model on external mixes;
- mining copyrighted DJ sets to infer automation;
- relying on learned automation that cannot be inspected or explained.

### 2.7 Commercial-compatible dependency posture

The implementation should avoid locking the core mixer into licensing choices that would
block future commercial use.

Rules:

- do not mandate GPL-only libraries in Core or the default production path;
- prefer permissively licensed DSP dependencies or self-contained code for basic EQ,
  gain, filters, and resampling;
- if a stronger time-stretch library is later useful, hide it behind an adapter and make
  license compatibility an explicit operator/build decision;
- document the license of any audio DSP dependency before merging it.

---

## 3. Non-goals

This phase does not build:

- stem separation;
- AI audio generation for transitions;
- neural transition prediction;
- imitation of existing DJ sets;
- live scratching;
- beat juggling;
- full club-DJ controller emulation;
- strong time-stretching across entire songs;
- real-time manual DJ control from the browser.

The target is **radio-grade automatic DJ mixing**, not a replacement for a human live DJ
controller.

---

## 4. Data model

### 4.1 `DjProfile`

A DJ host gets a profile that shapes set construction and transition aggressiveness.

```csharp
public sealed class DjProfile
{
    public Guid Id { get; set; }
    public Guid ModeratorId { get; set; }

    public DjEnergyPolicy EnergyPolicy { get; set; }
    public DjTransitionAggressiveness Aggressiveness { get; set; }
    public DjVocalPolicy VocalPolicy { get; set; }
    public DjTempoPolicy TempoPolicy { get; set; }

    public int PreferredTransitionBars { get; set; }       // e.g. 16 or 32
    public double MaxTempoChangePercent { get; set; }      // e.g. 4.0 initially
    public double MinBeatGridConfidence { get; set; }
    public double MinCueConfidence { get; set; }

    public bool AllowEqAutomation { get; set; }
    public bool AllowFilterAutomation { get; set; }
    public bool AllowTempoMatching { get; set; }

    public string? StylePrompt { get; set; }               // "late-night deep house radio DJ"
}
```

Example enums:

```csharp
public enum DjTransitionAggressiveness
{
    Safe,
    Balanced,
    ClubLike
}

public enum DjVocalPolicy
{
    AvoidVocalOverlap,
    AllowShortOverlap,
    IgnoreVocalOverlap
}

public enum DjEnergyPolicy
{
    SmoothArc,
    BuildUp,
    PeakAndRelease,
    KeepSteady
}
```

### 4.2 `TrackCuePoint`

Cue points are durable and can be automatic or manual.

```csharp
public sealed class TrackCuePoint
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }

    public CuePointKind Kind { get; set; }
    public double TimeSeconds { get; set; }
    public double Confidence { get; set; }

    public CuePointSource Source { get; set; }             // Automatic | Manual | Imported
    public string? Label { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}
```

Recommended cue kinds:

```csharp
public enum CuePointKind
{
    FirstAudio,
    FirstBeat,
    Downbeat,
    IntroStart,
    IntroEnd,
    MixIn,
    MixOut,
    BreakStart,
    Drop,
    VocalStart,
    VocalEnd,
    OutroStart,
    OutroEnd,
    CustomA,
    CustomB,
    CustomC,
    CustomD
}
```

### 4.3 `TrackMixAnalysis`

Phase 3a already introduces per-track analysis. DJ mixing needs richer analysis, but it
should still live in the same analysis family.

```csharp
public sealed class TrackMixAnalysis
{
    public Guid TrackId { get; set; }

    public double? Bpm { get; set; }
    public double BpmConfidence { get; set; }

    public string? MusicalKey { get; set; }
    public double KeyConfidence { get; set; }

    public string? BeatGridJson { get; set; }
    public string? DownbeatJson { get; set; }
    public string? PhraseGridJson { get; set; }

    public string? EnergyCurveJson { get; set; }
    public string? VocalDensityCurveJson { get; set; }
    public string? SpectralBalanceCurveJson { get; set; }

    public DateTimeOffset AnalyzedAt { get; set; }
    public string AnalyzerVersion { get; set; } = "";
}
```

Store large arrays as compact JSON initially. If they become hot-path bottlenecks, move
them to sidecar-owned binary analysis files referenced from SQLite.

### 4.4 `DjSetPlan`

A DJ set is planned as one coherent object.

```csharp
public sealed class DjSetPlan
{
    public Guid Id { get; set; }
    public Guid ModeratorId { get; set; }
    public Guid FormatId { get; set; }

    public DateTimeOffset PlannedFor { get; set; }
    public TimeSpan TargetDuration { get; set; }

    public string Vibe { get; set; } = "";
    public string EnergyArc { get; set; } = "";
    public DjSetStatus Status { get; set; }

    public List<DjSetTrack> Tracks { get; set; } = [];
    public List<DjTransitionPlan> Transitions { get; set; } = [];
}
```

### 4.5 `DjTransitionPlan`

This is the key contract between planning and DSP execution.

```csharp
public sealed class DjTransitionPlan
{
    public Guid Id { get; set; }
    public Guid DjSetPlanId { get; set; }

    public Guid FromTrackId { get; set; }
    public Guid ToTrackId { get; set; }

    public DjTransitionStrategy Strategy { get; set; }

    public double FromCueSeconds { get; set; }
    public double ToCueSeconds { get; set; }
    public int DurationBars { get; set; }

    public double? FromPlaybackRate { get; set; }
    public double? ToPlaybackRate { get; set; }

    public double BeatAlignmentConfidence { get; set; }
    public double CueConfidence { get; set; }
    public double OverallConfidence { get; set; }

    public string? AutomationJson { get; set; }
    public string? PlannerNotes { get; set; }
    public string? SafetyFallbackJson { get; set; }
}
```

### 4.6 `DeckAutomationPoint`

Automation is expressed against virtual decks, not against raw tracks.

```csharp
public sealed record DeckAutomationPoint(
    double TimeSeconds,
    DeckId Deck,
    double GainDb,
    double LowEqDb,
    double MidEqDb,
    double HighEqDb,
    double? HighPassHz,
    double? LowPassHz,
    double PlaybackRate);
```

Initial EQ range should be conservative, for example ±12 dB. Full kills can come later
if the filters sound clean.

---

## 5. Analysis sidecar extension

Use the existing `sidecars/analysis` service. Do not create a second DJ-analysis sidecar.

Recommended endpoints:

```text
POST /analyze/mix-track
  input: track file path or mounted media id
  output: bpm, beat grid, downbeats, phrase grid, key, energy curve, cue candidates

POST /analyze/transition-candidates
  input: fromTrackId, toTrackId, optional djProfile
  output: ranked cue pairs and strategy candidates

POST /validate/beatgrid
  input: track id + beat grid
  output: confidence, warnings, suggested corrections
```

The sidecar may use Python audio-analysis libraries internally, but the Orchestrator only
sees the stable HTTP contract.

Analysis should be backfilled offline by `AnalysisBackfillService`. A DJ set should not
block the live stream while analyzing an unknown track. If analysis is missing or weak,
the planner either skips the track for DJ sets or uses a safe cue-light transition.

---

## 6. DJ set planning pipeline

### 6.1 Set intent

The DJ host receives a `PromptContext` and produces high-level intent, not timing-level
DSP instructions.

Example intent:

```json
{
  "vibe": "late-night melodic electronic, steady but not peak-time",
  "energyArc": "start at 0.45, rise to 0.75, cool down in the final 10 minutes",
  "bpmRange": [118, 124],
  "avoid": ["vocal-over-vocal", "hard genre jumps"],
  "preferredTransitions": ["BassSwap32", "PhraseBlend16", "BreakToDrop"],
  "talkPolicy": "short intro, no talk during continuous 30 minute mix"
}
```

The parser validates this into a `DjSetIntent` object.

### 6.2 Candidate pool

Build a track pool from:

- current format genre/subgenre;
- DJ profile preferences;
- track votes and play count;
- track freshness/cooldowns;
- BPM/key/energy metadata;
- availability of reliable cue points.

Tracks without usable analysis may still play on normal shows, but should be penalized or
excluded from DJ sets until analyzed.

### 6.3 Sequence scoring

The deterministic planner scores track pairs.

Suggested scoring dimensions:

| Dimension | Purpose |
|---|---|
| BPM distance | prefer small tempo differences |
| key compatibility | avoid harsh harmonic clashes where key is known |
| energy delta | follow the requested energy arc |
| cue compatibility | mix-in/mix-out points line up musically |
| phrase compatibility | 8/16/32-bar boundaries align |
| vocal conflict risk | avoid vocal-over-vocal unless explicitly allowed |
| freshness | avoid replaying the same tracks too often |
| DJ profile fit | respect the host's style |

### 6.4 Transition planning

For each adjacent pair, create one or more candidate `DjTransitionPlan`s and choose the
highest safe score.

The final set should have no unvalidated transition. If a transition cannot be validated,
choose a different next track before accepting a poor transition.

### 6.5 Produce-ahead

DJ sets should be planned ahead of airtime.

The live path should only execute already-planned transitions. Expensive cue analysis,
track-pair scoring, preview rendering, and operator review should happen before the set
starts whenever possible.

---

## 7. Transition strategies

Initial strategy set:

| Strategy | Use case | Required confidence |
|---|---|---|
| `PhraseBlend16` | simple 16-bar beat-aligned blend | beat grid + phrase cue |
| `PhraseBlend32` | longer smooth mix | beat grid + phrase cue |
| `BassSwap32` | classic DJ low-end exchange | beat grid + phrase cue + low vocal risk |
| `IntroOutroBlend` | outgoing outro under incoming intro | intro/outro cue points |
| `BreakToDrop` | incoming drop lands after outgoing break/outro | drop cue + phrase confidence |
| `FilterOutPhraseCut` | safe phrase cut with filter movement | cue points, no tempo match required |
| `DjSafeFade` | conservative DJ fallback | rough cue points only |

Do not start with too many templates. Quality comes from a few reliable strategies, not a
large catalogue of brittle ones.

---

## 8. Tempo matching policy

Tempo matching is allowed, but bounded.

Initial default:

```text
0–2% BPM difference:
  allow simple playback-rate adjustment during transition

2–4% BPM difference:
  allow only if DJ profile permits tempo matching and beat confidence is high

>4% BPM difference:
  do not beatmatch in the first implementation; choose another transition or another track
```

The first implementation may use basic resampling for small changes. Strong
pitch-preserving time-stretching is not mandatory for this phase and should not be added
through a license-incompatible default dependency.

Prefer this behavior:

- tempo-adjust only the transition window;
- bring the incoming track back to native playback rate after the transition;
- never stretch an entire song just to force a set plan;
- never use tempo matching to hit top-of-hour timing.

---

## 9. Cue point rules

### 9.1 Automatic cue points

The analysis sidecar proposes cue points with confidence. Examples:

- `FirstBeat`
- `IntroEnd`
- `MixIn`
- `MixOut`
- `BreakStart`
- `Drop`
- `VocalStart`
- `VocalEnd`
- `OutroStart`

Automatic cue points are useful immediately but should be visibly marked as automatic.

### 9.2 Manual cue points

The Web UI should let an operator adjust cue points for important tracks.

Manual cue points should support:

- waveform view if practical;
- coarse seek + fine nudge;
- label and notes;
- approve/reject automatic suggestions;
- quick audition from cue;
- quick audition of planned transition.

### 9.3 Cue precedence

Order of trust:

1. manual approved cue;
2. imported cue if import support is later added;
3. automatic cue with high confidence;
4. automatic cue with low confidence only for safe fallback strategies.

### 9.4 Cue safety

Never trust a cue blindly if it creates obvious bad output.

Validation should check:

- cue is inside track bounds;
- cue is not inside digital silence unless silence is intentional;
- cue is not too close to track start/end for the requested transition;
- cue aligns with nearby beat/downbeat when beatmatching is requested;
- vocal collision risk is acceptable for the selected DJ profile.

---

## 10. Mixer execution

### 10.1 Virtual decks

DJ mixing needs a two-deck model:

```text
Deck A: current/outgoing track
Deck B: incoming track
```

The live playout can still expose a single Icecast stream. Internally, the mixer needs to
hold two decoded sources during a transition and apply automation to each deck.

### 10.2 Automation lanes

Minimum lanes:

- gain;
- low EQ;
- mid EQ;
- high EQ;
- playback rate.

Optional first-phase lanes if cheap and stable:

- high-pass filter;
- low-pass filter.

Do not add complex FX first. Delay, echo, reverb, flanger, scratch, and loop-roll style
moves can wait until basic DJ transitions are stable.

### 10.3 Sample accuracy

Automation execution belongs in the mixer/DSP layer, not in LLM code and not in UI code.

The planner emits time-indexed automation; the mixer applies it sample-accurately when
rendering the transition.

### 10.4 Live-stream safety

If execution fails during a live DJ set:

1. continue the current track;
2. cancel complex automation;
3. choose `DjSafeFade` or a clean phrase cut into the next track;
4. log the failed transition plan and reason;
5. notify Admin/Console.

The stream must not go silent because a transition plan was invalid.

---

## 11. DJ host behavior

A DJ host is still a personality, not just an algorithm.

The DJ can:

- introduce the set;
- explain the vibe briefly;
- mention track choices before or after the continuous mix;
- keep talk out of the middle of a continuous DJ block if the format requires it;
- remember successful sets and recurring style choices;
- have a recognizable mixing identity through `DjProfile`.

Example format policies:

```text
Club Mix:
  60 minutes, no talk except intro/outro, aggressive phrase blends allowed

Night Drive DJ:
  45 minutes, short intro, smooth energy arc, long blends, no vocal overlap

Lunch Mini-Mix:
  15 minutes, 4 tracks, safe transitions, one short host break at the end
```

---

## 12. Architecture placement

### Core

Core owns stable domain contracts and pure rules:

```text
ModeratorKind
DjProfile
TrackCuePoint
TrackMixAnalysis
DjSetPlan
DjTransitionPlan
DeckAutomationPoint
DjTransitionStrategy
DjTransitionSafetyRules
DjPairScorer
```

No EF, HTTP, Python, or UI dependencies.

### Infrastructure

Infrastructure owns persistence and adapter code:

```text
RadioDbContext entities/migrations
AnalysisSidecarClient extensions
TrackMixAnalysisRepository
CuePointRepository
optional DSP dependency adapters
```

### Orchestrator

The Orchestrator owns station behavior:

```text
DjSetPlanningService
DjTransitionPlanningService
DjSetProductionService
DjAwareShowRunner path
DjTransitionPreviewService
AnalysisBackfillService extensions
```

The Orchestrator decides when a DJ set is planned, when it is safe to air, and how to
fall back without breaking the stream.

### Web

The Web app stays thin and operator-focused:

```text
Host edit: ModeratorKind + DjProfile
Format edit: allows DJ Set format type
Track detail: cue point editor
DJ Set preview: sequence + transitions + confidence
Transition audition: approve/reject/manual correction
Admin/Console: DJ planning warnings
```

### Sidecar

`sidecars/analysis` provides audio facts. It does not own radio behavior and does not
choose what airs.

---

## 13. UI surfaces

### Host page

Add:

- host kind selector;
- DJ profile section when kind is `DJ`;
- mixing style presets;
- safe/balanced/club-like aggressiveness;
- maximum tempo adjustment;
- vocal-overlap policy;
- preferred transition length.

### Format page

Add a format type or capability:

```csharp
public enum FormatMixingMode
{
    StandardRadio,
    DjSet
}
```

A `DjSet` format requires a DJ host or a fallback DJ assignment rule.

### Track detail page

Add cue point management:

- cue list;
- automatic/manual badge;
- confidence;
- approve/reject;
- edit time;
- audition.

### DJ Set preview page

Before airing, show:

- selected track sequence;
- BPM/key/energy columns;
- transition strategy per pair;
- cue points used;
- confidence score;
- warnings;
- preview buttons for transitions;
- approve/replan controls.

Approval can be optional in autonomous mode but should exist for debugging and curation.

---

## 14. Configuration

Suggested flags/settings:

```text
Mixer__DjModeEnabled=false
Mixer__DjRequireApprovedCuePoints=false
Mixer__DjDefaultTransitionBars=16
Mixer__DjMaxTempoChangePercent=4.0
Mixer__DjMinBeatGridConfidence=0.70
Mixer__DjMinCueConfidence=0.60
Mixer__DjAllowFilterAutomation=true
Mixer__DjAllowTempoMatching=true
Mixer__DjPreviewRenderEnabled=true
```

DJ mode should ship behind a feature flag. Legacy playout and ordinary Phase 3a mixing
must remain available.

---

## 15. Milestones

### M1 — Host kind and DJ profile

- Add host kind/classification.
- Add `DjProfile` data model and settings.
- Add `FormatMixingMode` or equivalent.
- Ensure normal hosts keep standard transitions.
- Unit tests: DJ format requires a DJ host or defined fallback; regular formats do not
  invoke DJ planning.

### M2 — Cue point model and analysis extension

- Add `TrackCuePoint` and `TrackMixAnalysis` fields needed for DJ mixing.
- Extend analysis sidecar contract for cue candidates, beat/downbeat/phrase grids, energy
  and vocal-density curves.
- Backfill analysis asynchronously.
- Unit tests for cue precedence and validation.

### M3 — Deterministic DJ set planner

- Build `DjSetPlanningService`.
- Generate candidate track pool.
- Score track pairs by BPM, key, energy, cue compatibility, phrase fit, vocal risk, and
  freshness.
- Produce `DjSetPlan` with transition placeholders.
- No mixer automation required yet.

### M4 — Transition templates and automation plans

- Implement first strategies: `PhraseBlend16`, `PhraseBlend32`, `BassSwap32`,
  `IntroOutroBlend`, `FilterOutPhraseCut`, `DjSafeFade`.
- Emit `DjTransitionPlan` with automation JSON.
- Validate plans before execution.
- Unit tests for safety thresholds and fallback strategy selection.

### M5 — Deck automation execution

- Extend mixer execution to support two-deck transition automation.
- Implement gain and 3-band EQ lanes first.
- Add optional filter lanes only if stable.
- Add bounded playback-rate lane for small tempo changes.
- Golden-file or numeric DSP tests for no clipping, no NaN, no silence gap, and stable
  transition duration.

### M6 — Cue editor and transition preview UI

- Track cue point UI.
- DJ set preview UI.
- Transition audition endpoint.
- Approve/reject transition plans.
- Persist operator corrections.

### M7 — DJ ShowRunner integration and hardening

- A DJ-format slot creates or loads a `DjSetPlan` ahead of airtime.
- Live playout executes DJ transitions for the set.
- Failure path uses `DjSafeFade` or clean phrase cut, never dead air.
- Console/Admin surfaces show warnings and transition diagnostics.
- Soak test with a 30–60 minute DJ set.

---

## 16. Definition of Done

- [ ] A host can be marked as `DJ` and configured with a `DjProfile`.
- [ ] A format can require `DjSet` mixing.
- [ ] Regular hosts keep ordinary Phase 3a transitions.
- [ ] DJ-format music-to-music transitions go through the DJ planner.
- [ ] Cue points are stored, editable, and source/confidence-aware.
- [ ] Automatic cue points can be approved or overridden manually.
- [ ] A DJ set is planned ahead with a coherent track order and transition plan.
- [ ] The first transition templates work without stems.
- [ ] Gain and 3-band EQ automation execute through the mixer.
- [ ] Tempo matching is bounded and disabled when confidence or BPM distance is unsafe.
- [ ] Vocal-over-vocal risk is considered before selecting a transition.
- [ ] Bad analysis does not break the stream; it triggers safe DJ fallback or replanning.
- [ ] No ML model training, no external DJ-set mining, and no stem separation are part of
      the implementation.
- [ ] `dotnet build`, `dotnet test`, sidecar tests, and a 30-minute DJ-set soak pass.

---

## 17. Testing and diagnostics

### Core tests

- cue precedence: manual beats automatic;
- cue validation: out-of-range cues rejected;
- pair scoring: BPM/key/energy/vocal-risk affect ranking;
- transition strategy selection respects `DjProfile`;
- fallback selection never returns an unsafe null transition.

### Infrastructure tests

- analysis sidecar response parsing;
- EF migrations and entity persistence;
- automation JSON round-trip;
- invalid sidecar output is handled safely.

### Orchestrator tests

- DJ format invokes `DjSetPlanningService`;
- regular format does not;
- missing analysis triggers replan or safe fallback;
- planned set can be recovered after restart;
- failed transition logs a warning and keeps playout alive.

### Mixer/DSP tests

- no clipping beyond configured limiter expectations;
- automation envelopes are monotonic where intended;
- transition duration matches plan;
- playback-rate changes stay within allowed bounds;
- output contains no NaN, infinities, or all-zero gaps.

### Manual verification

- 15-minute safe DJ set;
- 30-minute balanced DJ set;
- one set with manually corrected cue points;
- one set with a deliberately bad cue to verify fallback;
- one normal host show to verify standard transitions are unchanged.

---

## 18. Open questions

- Should `ModeratorKind` be a single enum, or should host capabilities be modeled as
  flags such as `CanHostWeather`, `CanHostDjSet`, `CanJoinPodcast`?
- Should DJ sets be rendered fully ahead as a single WAV, or executed live from planned
  transitions? Recommendation: execute planned transitions live first; optionally add
  offline render later for archival/replay sets.
- How much manual cue editing is needed in the first UI: list editor only, or waveform
  editor immediately?
- Should harmonic key compatibility be strict, soft, or only advisory in early versions?
- Should DJ sets allow talk inside the mix, or only intro/outro by default?
- Should transition ratings affect only the same track pair, or similar pairs through
  transparent deterministic rules?

---

## 19. Recommended first cut

Build the smallest version that can sound intentionally DJ-like:

1. DJ host kind + DJ format mode.
2. BPM, beat grid, intro/outro, cue points.
3. Manual cue override.
4. `PhraseBlend16`, `BassSwap32`, and `DjSafeFade`.
5. Gain + low/mid/high EQ automation.
6. Bounded tempo matching up to 4%.
7. Transition preview and approval.
8. A 30-minute DJ-set soak test.

Do not add stems, ML learning, scratches, echo tricks, or strong time-stretching until
this first cut is reliable.
