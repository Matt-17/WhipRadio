# WhipRadio — Phase 0: Locked Technical Decisions

> A single source of truth for cross-cutting decisions, sitting above every phase plan.
> When a phase plan and this document disagree on one of these points, **this document
> wins**. Update it deliberately; don't let decisions drift silently.

---

## Reference hardware (development / homelab target)

- **GPU: NVIDIA RTX 4070, 12 GB VRAM.** This is the design target. Everything must run
  here with the LLM responsive. A CPU-only / smaller-GPU profile must also exist
  (slower generation is acceptable per the project's "talk fills gaps" tolerance).

The 12 GB ceiling drives the model and VRAM-budget decisions below.

---

## LLM: Gemma 4 E4B (via Ollama)

- **Default model: Gemma 4 E4B** (~4.5B effective). Multilingual (incl. German),
  Apache 2.0 licensed (matters for the Phase 8 commercial question), strong at the
  project's actual workload (text generation, summarisation, banter — not coding).
- Pull via Ollama; keep the provider behind the existing interface/factory so a model
  can be swapped per role.
- **Per-role models (optional, encouraged):** a small fast model for host one-liners; a
  heavier reasoning model for the Program Director and multi-agent talks. On 12 GB the
  heavier option (e.g. Gemma 4 26B A4B MoE) generally won't co-reside with TTS+music —
  use it only if you accept evicting music during director reasoning, or run it on a
  bigger machine. **For the 12 GB target, E4B everywhere is the safe default.**
- **Qwen 3.5** is the sanctioned alternative to A/B-test specifically for Program
  Director reasoning (hybrid thinking mode). Keep the door open; don't block on it.
- **Kimi and other ~trillion-parameter models are out** — not single-GPU viable.

### Context size — the real VRAM lever
- The model weights are modest; the **KV cache for context is what eats VRAM**. Do **not**
  allocate the full 128K context.
- **Set a working context (`OllamaContextSize`, already a Phase 2 setting) to ~16K–32K
  tokens.** That comfortably holds a full podcast/talk transcript, which is the longest
  thing we need. Long-ago memory is intentionally out of scope (handled by the distilled
  memory layers in Phase 3b, not by raw context).

### VRAM budget on 12 GB (planning estimate, verify in practice)
| Component | Residency | Rough VRAM |
|---|---|---|
| Gemma 4 E4B (Q4_K_M quant) | always resident | ~3.5 GB |
| KV cache @ ~16–32K working context | always resident | ~0.5–1.5 GB |
| TTS (Kokoro/Piper, local) | resident if room | ~0.5–1 GB |
| ElevenLabs TTS | API — no VRAM | 0 |
| Music (ACE-Step) | **on-demand / evictable** | ~remaining 6–7 GB |

**Firm rules from this budget:**
1. **LLM + local TTS stay resident** (responsiveness is the priority the user named).
2. **Music is the swapper** — it may load on demand and be evicted; slow is acceptable.
3. **Serialise heavy music generation against heavy LLM reasoning** via the shared
   generation semaphore (already specified in Phase 3a's backfill) so both don't spike
   VRAM simultaneously.
4. Use a **Q4_K_M** (or similar) quant for the LLM; don't run full precision on 12 GB.

---

## MCP: explicitly NOT used

- The self-built `Aktion()` protocol (Phase 4) is the internal control mechanism. It is
  function-calling we own end-to-end; a tolerant parser is more robust for local models
  than a strict protocol, with no overhead.
- **MCP is dropped.** Do not introduce it as the core action mechanism. (If, far later,
  there's a concrete need to consume third-party tool servers or expose WhipRadio to
  external agents, revisit via an adapter behind `IActionParser` — but not now.)

---

## Audio: one DSP core, two contexts

- **`MixerCore` (Phase 3a) is pure DSP on buffers** and is reused in two places:
  - **Live:** `AudioMixerService` → Icecast.
  - **Offline:** `SegmentRenderer` premixes multi-speaker talk/podcast segments into a
    single WAV (see Phase 5). The live system then plays that segment as one item.
- Multi-speaker **cross-talk/overlap is rendered offline at production time**, not by the
  live mixer — deterministic and fully controllable.

---

## Voice consistency: "by construction", not post-hoc matching

- Each speaking entity (host, band member, caller) has a `VoiceProfile` (gender + coarse
  timbre descriptors). **Both** the TTS voice choice **and** the ACE-Step vocal prompt are
  derived from the same profile, so speaking and singing voices are *consistent*, not
  identically *matched*. (Real singers sound different speaking vs singing anyway, so
  perfect identity is neither achievable across two engines nor even desirable.)
- True cross-engine voice cloning (same voice sings and speaks) is a **stretch goal**,
  not a requirement.

---

## Deferred-but-designed-for (don't build yet; don't block either)

These are confirmed *later* features. Design data shapes so they slot in without rework,
but do not implement until their phase:

- **Telephone / lo-fi caller voices** — a `VoiceFx` post-processing chain (band-pass +
  light compression + codec noise) applied after TTS. A caller = a guest with a phone
  `VoiceFx` preset; a host can "call in" with the same preset. Build the `VoiceProfile`
  with an optional `Fx` field now; implement the filter later.
- **Group cross-talk (3–5 speakers talking over each other)** — the hard part is the
  LLM describing chapters in enough detail to mark *what overlaps when* and still sound
  natural. The `SegmentRenderer` + bounded overlap is the mechanism; the rich
  choreography is a later refinement. Keep `ConversationSegment` turns stored as
  `(speakerId, text, markers, timing?)` so overlap timing can be added without a schema
  change.

---

## Standing constraints (unchanged, restated for one-stop reference)
- Never break the live stream; new subsystems ship behind flags where risky.
- Copyright discipline: paraphrase external facts; never reproduce article text or lyrics.
- `dotnet build` + `dotnet test` stay green every milestone.
- Everything local except declared external data sources (weather, news, traffic,
  optional OpenAI/ElevenLabs/Twitch).
