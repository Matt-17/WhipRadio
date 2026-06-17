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

**Planned:** offline multi-speaker rendering belongs to later conversation/artist phases.
The future `SegmentRenderer` should reuse `MixerCore` to premix multi-speaker talk or
podcast segments into a single WAV. The live stream then plays that rendered segment as
one item.

## Voice Consistency: VoiceProfile Planned

Current host voice fields are interim. The intended model is a structured
`VoiceProfile` for each speaking entity: gender, coarse timbre descriptors, resolved
TTS voice, and optional future FX chain.

**Planned:** Phase 5 artist/band work introduces rich artists, band members, guests,
`VoiceProfile`, and `VoiceFx`. Speaking and singing voices should be consistent by
construction: both TTS selection and music vocal prompting derive from the same
profile. Exact cross-engine voice cloning remains a stretch goal, not a requirement.

## Standing Constraints

- Never break the live stream; risky subsystems ship behind flags or queues.
- Copyright discipline: paraphrase external facts; never reproduce article text or
  lyrics.
- `.\build.ps1` and `.\test.ps1` stay green at each milestone.
- Everything is local except declared external sources/providers: weather, optional
  OpenAI/ElevenLabs, optional remote studios, and future explicitly configured sources.
