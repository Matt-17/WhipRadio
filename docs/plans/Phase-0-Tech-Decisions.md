# WhipRadio - Phase 0: Locked Technical Decisions

This document is the cross-phase decision register. When a later phase plan disagrees
with a point here, this file wins until it is deliberately revised. If a decision is
accepted but not implemented yet, track the implementation work in
`Phase-0-Deferred.md` instead of pretending the current code is the final design.

## Decision Status

- **Firm**: architectural direction for all phases.
- **Planned**: accepted design, implemented in a named later phase.
- **Deferred implementation**: accepted decision, current program still needs work.
- **Superseded**: old direction replaced by a newer decision in this file.

## Reference Hardware And Deployment Model

The development/homelab target remains an **NVIDIA RTX 4070 with 12 GB VRAM**.
Everything must be capable of running on that class of machine, with slower CPU-only
fallbacks where practical.

The architecture now supports **multiple studios** for music and voice generation.
Studios may run on the same machine as WhipRadio, on another local machine, or behind
an online API. The operator owns where those studios run and how much hardware they
have. WhipRadio should discover, book, and monitor studios, but it must not assume it
controls every GPU in the deployment.

For a single 4070 machine, the old VRAM budget remains useful as operator guidance:

| Workload | Priority | Notes |
|---|---|---|
| LLM | highest | Keep responsive; do not allocate huge context by default. |
| Local TTS | high | Prefer resident when it shares the app machine. |
| Music studio | medium | Can be slow, queued, or moved to another machine. |
| Image studio | lowest | Phase 6b; never real-time. |

## International Product And Language Baseline

**Firm decision:** WhipRadio is mandatory international software. Defaults and seeded
examples should be English-first, with US/global assumptions as the primary baseline
because the product is tech-oriented.

Region-specific behavior belongs behind configuration. Plans, prompts, seed data,
source lists, and UI copy should not bake in a local market as the default experience
unless an operator explicitly configures it.

## Music Generation Quality Defaults

**Firm decision:** the single-GPU default favors better ACE-Step output over maximum
throughput. Local ACE-Step uses `acestep-v15-turbo` with LM thinking enabled and 12
inference steps. Station settings default generated songs to 150-480 seconds; music
jobs are queued, so slower generation is acceptable when it improves quality.

The ACE-Step sidecar image pins the PyTorch stack to the stable CUDA 12.8 wheels
(`torch==2.8.0`, `torchvision==0.23.0`, `torchaudio==2.8.0`) after upstream
dependency sync. This avoids accidental CPU-only installs and avoids drifting to
newer torch builds whose ACE-Step behavior has not been validated on the 4070
target.

Every artist-produced song uses one artist-owned planning step before recording,
regardless of whether it was triggered manually, by listener request, or by automatic
library stocking. The artist chooses title, detailed style, language, vocal vs.
instrumental form, lyrics when vocal, and target duration from their biography,
signature style, and previous songs with listener likes/dislikes. The artist also
writes a short in-universe song story explaining why and how the song was created;
that story is stored on the track for future host intros/outros. Instrumental-only
studios such as MusicGen constrain the plan to instrumental output, but host playlist
preferences no longer decide whether an artist writes a vocal song.

**Implemented:** artist roster capability also constrains vocal planning. A song may
only be planned as vocal when the artist has an explicit vocal member and an ACE-Step
recording studio is available. Instrumental-only acts, including bands with members
but no vocal role, are forced to instrumental plans and generation prompts explicitly
ban lead vocals, backing vocals, choir, chants, spoken word, and sung words.

**Implemented:** ACE-Step vocal tracks now use artist voice continuity. WhipRadio
prepares a hidden Qwen-designed spoken reference for the lead vocalist and always
uploads that pre-generated TTS voice reference as `ref_audio` for vocal ACE-Step
songs. Existing sung ACE-Step tracks are not used as the handoff reference. If the
spoken reference is not ready, manual song requests stay queued at the front and
automatic production skips the cycle instead of generating a vocal song without a
reference.

**Removed:** ACE-Step artist-voice LoRA was evaluated and dropped. A single-song
LoRA did not clone the voice, while the short Qwen-designed spoken `ref_audio` clip
transferred the voice well at a fraction of the cost. The LoRA train/preprocess/
export/load/scale/toggle code, its `AceStepLora*` request fields, and its config were
removed. Sung/spoken `ref_audio` is the sole voice-continuity mechanism.

## ACE-Step Sidecar: Decode Quality And Liveness

**Implemented:** the ACE-Step sidecar image sets `ACESTEP_VAE_DECODE_CHUNK_SIZE=256`.
On a ≤12 GB GPU the auto decode chunk size is 128 while the tile overlap is fixed at
64, so `chunk_size − 2·overlap == 0` — the decoder force-halved the crossfade to 32 and
logged `[tiled_decode] Reduced overlap from 64 to 32` on every song. At 256 the full
64-frame crossfade is valid (`256 − 128 = 128`), giving larger tiles, smoother audio,
and no warning. Decode peak was ~8.9 GB on the 12 GB card; lower to 192 if OOM
fallbacks ever trigger. Model/checkpoint caches must live under the persistent
`/models` bind, not the container's writable layer, so recreating the container does
not re-download the 5Hz LM.

**Operational rule:** do not keep the 5Hz LM resident in VRAM during VAE decode. On the
12 GB card in CPU-offload mode, a 207 s decode with the LM pinned alongside the DiT
thrashed for 9+ minutes at ~310 MB free; with only the DiT initialized (LM loaded
transiently then offloaded, ~5.8 GB free) the same song decoded in ~8 s. The
orchestrator must not force-init the LM for generation, and the LM must be offloaded
before the decode tile loop.

**Implemented:** the sidecar supervises its own liveness so a wedged queue cannot brick
generation while `/health` stays green. The image `CMD` is `whip_watchdog.py` (PID 1):
it spawns `api_server` as a child, polls `/v1/stats` every 30 s, and if jobs are
pending but none reach a terminal state for `ACESTEP_STUCK_TIMEOUT_SECONDS` it kills
the server and exits. The container runs `--restart unless-stopped`, so the exit brings
up a fresh server — the whole recovery loop lives in the sidecar and survives a
published deployment. Layered timeouts, with the invariant **STUCK > GENERATION_TIMEOUT**
so a legitimately long song is never mistaken for a wedge:

| Knob | Where | Value | Meaning |
|---|---|---|---|
| `ACESTEP_GENERATION_TIMEOUT` | sidecar env | 600 s | hard cap for one song; overrun → job `failed` (a terminal state) |
| `ACESTEP_STUCK_TIMEOUT_SECONDS` | watchdog env | 1200 s | wedge detector → kill + container restart |
| `GenerationTimeout` | orchestrator `appsettings` | 45 min | the app's outer patience on a request |

`start-studios.ps1` creates studio containers with the watchdog CMD,
`--restart unless-stopped`, and the 10/20-min timeouts. Orchestrator-side recovery
(`StudioMusicGenerator` catching `TimeoutException` → `StudioDockerControl.TryRestartAsync`,
plus the studios-page restart button) is now a secondary dev/operator convenience, not
the safety net.

## LLM: Gemma 4 E4B Via Ollama

**Firm decision:** the default local text model is **Gemma 4 E4B via Ollama**. Runtime
defaults, docs, and seed/config examples should use the `gemma4:e4b` Ollama tag.
Ollama is a long-lived operator-owned Writer Room service, started with
`start-studios.ps1` on `http://localhost:8001` by default (host port 8001 maps to
the container's native 11434, matching the 8x01 studio port layout). The Aspire
AppHost consumes `Llm__Endpoint`; it does not create or own the Ollama container.

Provider routing must stay behind `ITextGenerationService` so roles can later use
different providers or models. Per-role model selection is still encouraged:

- Fast one-liner model for small host copy.
- Heavier reasoning model for the Program Director or multi-agent talks.
- Qwen remains a sanctioned A/B candidate for reasoning roles.

Kimi-scale or trillion-parameter models remain out of scope for the single-GPU target.

## Ollama Context Size

**Firm decision:** expose an explicit `Llm__ContextSize` setting and pass it to
Ollama as `num_ctx`.

The working default is **16K tokens**, not the full maximum context a model advertises.
The model weights are not the only VRAM cost; the KV cache grows with context length
and can crowd out TTS/music. Operators may raise this on larger GPUs or remote LLM
studios. Long-term memory should be distilled and retrieved through later memory
layers, not carried as raw context forever.

## Studios And Heavy Work Coordination

The earlier "one shared generation semaphore" idea meant: before a heavy job starts,
it checks with one shared gate so two expensive jobs do not overload the same GPU at
the same time.

With distributed studios, a single global semaphore is no longer the right model.
WhipRadio should use:

- **Per-studio booking** for concrete studio jobs.
- **Capacity/resource grouping** for studios that share the same physical machine or
GPU, if the operator declares that relationship.
- **A local workload coordinator** only for resources the app actually owns or can
reason about.

The app should not try to evict or pause a remote studio's models unless that studio
explicitly exposes such controls.

## Image Generation: Phase 6b

**Planned:** generated photography is handled by Phase 6b. The model decision remains
FLUX.2 Klein 4B or the closest compatible Apache-licensed local image model that fits
the hardware when implementation begins.

Images are generated, curated, cached, and referenced through generated-image records.
There should be **no manual `PhotoUrl` field and no upload path** as the primary design.
Until generated images exist, the UI shows skeleton placeholders.

Image generation is lowest priority and never part of the live audio path.

## MCP: Explicitly Not Used

The self-owned action protocol is the internal control mechanism. MCP is not the core
action system. If WhipRadio later needs to consume third-party MCP servers or expose
tools to external agents, that should be an adapter behind the action/parser boundary,
not a replacement for the internal protocol.

## Audio: Mixer Core Now, Offline Rendering Later

`MixerCore` is the pure DSP core for sample-buffer mixing. The live mixer may use it
directly for crossfades, ducking, and transition diagnostics.

**Implemented:** live playout persists the active item plus listener-facing next-up
queue under the configured data root. On process restart, WhipRadio restores the same
timeline before the show runner refills anything; the active item resumes from its
elapsed wall-clock offset, and queued scheduled items such as weather stay in order.

**Implemented:** listener-facing now-playing updates are intentionally delayed by
`Stream__DisplayLatencySeconds` so titles, timers, queue changes, play log entries,
and Icecast metadata line up with what the browser stream is actually playing. The
development calibration is 5 seconds; operators should tune this value when a
deployment's Icecast/browser buffering differs.

**Implemented in Phase 3c:** top-of-hour packages are timed speech packages, not hard
cuts. The mixer consumes a scheduled interrupt, fades active sources over
`TopOfHourFadeOutSeconds` (default: 1 second), then starts the package within
`TopOfHourIntroGraceSeconds` when a suitable intro/handoff is ready. Legacy playout
falls back to queue-front insertion.

**Implemented:** top-of-hour block production uses a segment-contributor architecture
(`ITopOfHourSegmentContributor`). Each segment type (news, weather, future
traffic/Wikipedia/sports) is a self-contained contributor registered in DI. The
orchestrator collects all enabled contributors, picks the soonest cadence boundary
across them, and asks each whether it airs at that target. This supports arbitrary
cadence combinations (e.g. news=60/weather=30 produces a full block at :00 and a
weather-only package at :30) without per-segment special-casing.

Each contributor produces its own LLM-written intro handover (with direct-text
fallback) and body independently. One segment's failure never drops another
segment's intro. Degraded packages retain their planned label ("Top of hour") with a
recorded `FailureReason` — they are never silently relabeled as weather-only. The
ShowRunner gap-talk weather path defers to scheduled packages to avoid double-airing.

Adding a new segment type requires: a new contributor class implementing
`ITopOfHourSegmentContributor`, a `SpecialistHostRole` enum value, per-segment
settings columns + EF migration, a handoff prompt template, and DI registration.
The orchestrator, dispatcher, guards, and renderer do not change.

**Implemented in Phase 3b:** station branding now includes slogan, vision, and mission
as prompt context. Generated jingles are short instrumental station identity sources
created through the existing ACE-Step recording backend and stored under the shared
audio library as `Jingle` records. They are modeled as TalkBreak parts (`Jingle`) for
ordered spoken segments.

**Implemented in Phase 3b:** `SegmentRenderer` can render an ordered 1-N set of
already-produced TalkBreak parts into one composite announcement WAV and preserve
part metadata for PlayLog expansion. This is the single-speaker/single-track
compositor used by ordinary talk chains.

**Planned:** offline multi-speaker rendering belongs to later conversation/artist phases.
The expanded renderer should reuse `MixerCore` to premix multi-speaker talk or podcast
segments into a single WAV. The live stream then plays that rendered segment as one
item.

## Voice Consistency: VoiceProfile Planned

Current host voice fields are interim. The intended model is a structured
`VoiceProfile` for each speaking entity: gender, coarse timbre descriptors, resolved
TTS voice, and optional future FX chain.

**Planned:** Phase 5 artist/band work introduces rich artists, band members, guests,
`VoiceProfile`, and `VoiceFx`. Speaking and singing voices should be consistent by
construction: both TTS selection and music vocal prompting derive from the same
profile. Exact cross-engine voice cloning remains a stretch goal, not a requirement.

**Partly implemented:** artist creation now stores member biographies, voice-creation
prompts, hidden Qwen voice ids, and saved spoken reference WAV paths for every member.
The full structured `VoiceProfile` with FX remains planned.

**Implemented in Phase 3c:** new hosts created through the web UI use the active
local voice booth's voice-design path. The UI no longer asks operators to choose a
TTS engine/model for normal host hiring. If no active voice-design-capable booth is
available, host creation fails with an explicit operator error instead of falling
back to a preset Kokoro voice. Existing host rows keep their stored engine and voice
ids until deliberately redesigned.

**Implemented in Phase 3c:** news and weather specialist hosts are planned by the
program director from structured JSON, using station name, slogan, vision, mission,
active format/audience context, and the optional operator hint. The UI does not
collect name or gender for specialist hiring; if no suitable specialist exists when
production needs one, the director creates and assigns one instead of skipping the
desk by default.

**Implemented in Phase 4:** director chat host hiring uses the same
`SpecialistHostCreationService` path for general hosts. The operator provides only
a short brief; the director/writer-room path chooses persona, traits, gender, and
voice description, and the active voice booth resolves the TTS voice. Chat does not
add name lists, gender pickers, model pickers, or voice-id fields.

## Encoder / Icecast resilience

PlayoutService no longer hot-loops ffmpeg into a dead Icecast mount. The restart
loop now uses exponential backoff (5s → 10s → 20s → 40s → 60s cap by default,
configurable via `Stream__EncoderInitialBackoffSeconds` /
`Stream__EncoderMaxBackoffSeconds`) and a crash-rate circuit breaker: after
`Stream__EncoderCrashThreshold` (default 5) encoder crashes inside a
`Stream__EncoderCrashWindowMinutes` (default 5) rolling window, the station is
**parked** — `PlayoutEnabled` is flipped to false and a `StationStatus.Offline`
state is pushed to the UI lamp — and stays parked until an operator re-enables
On Air. A session that runs longer than
`Stream__EncoderSuccessResetsAfterSeconds` (default 120) before crashing clears
the streak, so an unrelated late crash is treated as a fresh incident rather than
a hot-loop.

The derived encoder/stream state (`Online` / `Reconnecting` / `Offline`) is held
in `IStationStatusReporter`, pushed to the web app via the `StationStatusChanged`
RadioHub event and snapshotted at `GET /api/station/status`. The On Air lamp
reflects it (green-red live, amber reconnecting, fast-red offline). This is
distinct from `PlayoutEnabled` (operator intent) and the `EncoderHeartbeat`
liveness probe.

## Emergency audio fallback

When normal planning/generation stalls and the live queue is empty at an item
boundary, playout may reuse an already generated local track as emergency
fallback audio. This path is deliberately dependency-free: it does not call the
LLM, TTS, news, music generation, or analysis services, and it does not create
new media. First startup can still warm up with silence until at least one valid
generated track exists.

Fallback plays are regular track plays for counters and listener metadata, but
the play log persists `WasFallback=true` so the UI can mark exactly which plays
were delivered by the UPS path.

## Standing Constraints

- Never break the live stream; risky subsystems ship behind flags or queues.
- Copyright discipline: paraphrase external facts; never reproduce article text or
  lyrics.
- `.\build.ps1` and `.\test.ps1` stay green at each milestone.
- Everything is local except declared external sources/providers: weather, optional
  OpenAI/ElevenLabs, optional remote studios, and future explicitly configured sources.

## Music Selection Diversity

**Firm decision:** the same song must not be chosen twice within a format's
back-to-back shows. The track selector hard-excludes every track aired since the
start of the previous show (the `ShowWindows.ExclusionSinceUtc` cutoff). When the
library is small and that window would empty the pool, the previous-show layer is
dropped first, keeping only the short recent window
(`StationSettings.RecentExclusionCount`, default 5) excluded — graceful relaxation
that preserves the no-immediate-repeat guarantee.

Selection rules are **format-aware**, driven by a structured `FormatSelectionRules`
owned type on `Format`, produced once at format-creation time by an LLM that reads
the director's free-text `Format.Description`. An "artist feature" format locks to
one artist (`SingleArtistFeature`); a "theme night" leans on a keyword
(`ThemeBlock`); an "eclectic mix" skips the genre filter (`Freeform`); everything
else uses `StandardRotation` with artist-repeat caps and subgenre variety. The
modes relax deterministically (`SingleArtistFeature` → `SpotlightArtist` →
`StandardRotation`) before the no-repeat window is ever dropped.

The whole feature ships behind `StationSettings.SelectionDiversityEnabled`
(default on). When off, the selector falls back to the legacy last-N exclusion
behavior. The host prompt receives the current and previous show's aired tracks
with an explicit "do not reintroduce or back-announce these as if new" instruction,
mirroring the existing `AlreadySpokenContext` anti-repeat pattern.

## Top-Of-Hour Timing And The Long News Format (Phase 3c.1 completion)

**TimingPlanner (enqueue-time strategy).** `ShowRunnerService` consults the pure
`TimingPlanner` (`src/WhipRadio.Core/Playout/TimingPlanner.cs`) each cycle when a
scheduled package (any kind) is within reach: (1) cap the next track pick at the
remaining gap plus the finish grace (soft filter in `WeightedTrackSelector` via
`SelectionSettings.MaxTrackDurationSeconds` — never empties the pool, the caller
re-checks the pick), (2) bridge sub-track gaps with a station-ID jingle (best fit,
least-recently-used rotation), (3) inside the intro grace stop enqueueing and let
the dispatcher claim the boundary, (4) the mixer's timed-interrupt fade stays the
last resort. Every non-neutral decision logs its reason. Music is never
time-stretched. The queue-ahead estimate comes from
`PlayoutStateStore.SnapshotTimeline()` (encoder-time truth, not the latency-delayed
now-playing state).

**Jingles air now.** `PlayoutItemType.Jingle` is a first-class playout item
(pairs like music in the `MixPlanner`, bumps `Jingle.PlayCount`/`LastUsedAtUtc`
via the `PlaybackReporter`, shows as a "jingle" tag in queue/play log). Jingles
are exempt from the top-of-hour track holds — a bridge jingle may start inside
the hold window.

**Long news format.** The scheduled ~30-minute news show is a program-grid block:
enabling `NewsLongFormatEnabled` (News page: air times CSV + duration 30–60 min)
makes `NewsShowScheduleSeeder` maintain one "The News Desk" `Format`
(`SelectionMode.NewsShow`, pure discriminator) plus a daily `ProgramSlot` per air
time; disabling removes exactly those seeded slots. Production reuses the
top-of-hour package pipeline: `NewsLongFormatSegmentContributor` selects a larger
topic-balanced pool (8/category, 48 total) and writes 2–7 topic chapters (~4 min
each) as independent write+voice jobs; a failed chapter shortens the show instead
of killing it. `NewsPackageKind.LongFormat` packages replace the short bulletin at
their boundary, get a 60-minute prepare-ahead runway and a 40-minute production
budget, and land through the unchanged dispatcher/timed-interrupt machinery.

**News beds.** The long show is mixed over a dedicated instrumental news bed:
`JingleKind.NewsBed` assets generated through ACE-Step (Branding page, 30–120 s
loopable underscore, no vocals — same model/license situation as station IDs).
The offline `BedMixer` (`src/WhipRadio.Core/Audio/BedMixer.cs`) loops the bed
across the block, ducks it ~−14 dB under speech with linear ramps, lets it
breathe in the chapter gaps, and streams the composite chunk-wise to disk (a
30-minute mix never sits in memory). Beds are adapted to the TTS PCM layout by
channel downmix + linear resampling. No bed available → the block airs spoken-only
with a logged degradation reason.

## Conversations And Podcast Shows (Phase 3c.2)

**Conversation engine.** Multi-speaker talks/podcasts are `ConversationSegment`s:
one schema-constrained LLM call (`ConversationScriptWriter`,
`Prompts/speech/ConversationWriter.*`) writes the whole speaker-tagged script;
each turn is voiced with the speaker's own designed `qv-` voice (hosts and artist
members share the identical Qwen TTS path); `ConversationAssembler` joins the
turn WAVs with short silence gaps (v1 rule: simple ordered spacing, no beds) and
adapts mismatched PCM layouts instead of failing. Turns persist as
generation-agnostic `ConversationTurn` JSON records so a later multi-agent
dialogue engine changes the writer, not the schema. Produced audio lives in
`library/conversations/` (outside both cleanup sweeps) wrapped in a
`ScheduledOnly` `AnnouncementKind.Conversation` announcement. Resume is
status-level only: a restart mid-voicing re-voices from the persisted script.
Guests without a designed voice are allowed — production enqueues priority voice
design and waits.

**Podcast shows are grid formats (firm decision).** Each `PodcastShow` owns one
weekly `ProgramSlot` via a seeded `Format` (`SelectionMode.PodcastShow`, pure
discriminator like `NewsShow`), maintained by `PodcastShowScheduleSeeder`.
`ConversationProductionService` fills every upcoming occurrence (90-minute
prepare-ahead, 60-minute budget, airtime-ramped GPU priority) and
`ConversationDispatcher` lands the episode at slot start. A slot with no
produced episode falls back to normal music under the show's format (logged).
A podcast slot owns its boundary outright: the news pipeline skips any package
target that coincides with an enabled podcast occurrence.

**Multi-slot timed interrupts.** `TimedPlayoutInterruptService` now holds a
LIST of pending interrupts (news + podcasts with different targets), releases
the earliest due one inside its claim window, and supports a targeted
`Clear(itemId)` so a news recreate cannot drop a pending podcast episode. All
approach-timing machinery (TimingPlanner track capping, jingle fill, playout
holds, mixer guard) treats scheduled conversation targets exactly like news
package targets.
