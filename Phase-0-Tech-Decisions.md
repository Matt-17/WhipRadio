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

**Implemented:** ACE-Step vocal tracks now use artist voice continuity. From the
second vocal ACE-Step song onward, WhipRadio uploads the best previous vocal track as
`ref_audio` so the same singer can be anchored immediately. When prior vocal tracks
exist, WhipRadio also prepares a curated per-artist ACE-Step LoRA dataset and tries to
load or train the matching adapter before recording the next song. Adapter artifacts
live under the ACE-Step `/models` volume, while curated source copies live under the
station data root mounted into the ACE-Step container at `/app/data`.

## LLM: Gemma 4 E4B Via Ollama

**Firm decision:** the default local text model is **Gemma 4 E4B via Ollama**. Runtime
defaults, docs, and seed/config examples should use the `gemma4:e4b` Ollama tag.
Ollama is a long-lived operator-owned Writer Room service, started with
`start-studios.ps1` on `http://localhost:11434` by default. The Aspire AppHost
consumes `Llm__Endpoint`; it does not create or own the Ollama container.

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

**Partly implemented:** artist creation now stores member biographies and voice-creation
prompts as Phase 3c/5 seed data. These are descriptive prompts only; the structured
`VoiceProfile` with resolved voice ids and FX remains planned.

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

## Standing Constraints

- Never break the live stream; risky subsystems ship behind flags or queues.
- Copyright discipline: paraphrase external facts; never reproduce article text or
  lyrics.
- `.\build.ps1` and `.\test.ps1` stay green at each milestone.
- Everything is local except declared external sources/providers: weather, optional
  OpenAI/ElevenLabs, optional remote studios, and future explicitly configured sources.
