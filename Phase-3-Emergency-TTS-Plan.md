# WhipRadio — Phase 3 (Emergency Insert): Qwen3-TTS Engine

> **Why this is an "emergency insert":** Phase 3a (mixer) is nearly done and the loudness
> normalization there assumes consistent, good speech. The current TTS quality is the
> weakest link, so we upgrade it *now*, before building more on top of it. This is a
> focused, prescriptive insertion — not a full phase.
>
> **The whole point of the Phase-1 `ITtsEngine` abstraction was this moment.** Nothing in
> the orchestrator's calling code should change. We add an engine, expose new
> capabilities, and migrate voices.
>
> Respects Phase 0: RTX 4070 / 12 GB target, LLM + local TTS resident, music swaps.

---

## 1. Decision

- **Adopt Qwen3-TTS** (Alibaba, open weights, Jan 2026) as the new primary local TTS.
- **Default model: `Qwen3-TTS-12Hz-0.6B`** on the 12 GB target (leaves headroom next to
  Gemma 4 E4B + music). **`1.7B` is selectable** for machines with more VRAM or when
  music is allowed to evict. Make the size a setting.
- German is first-class (one of the 10 supported languages), which fixes the project's
  long-standing weak-German-voice problem.
- **Keep Kokoro / Piper / ElevenLabs engines in place.** `ITtsEngine` already supports
  multiple engines; do not rip them out. Qwen3-TTS becomes the new default; others remain
  as fallback/options (Piper stays a useful low-resource fallback; ElevenLabs stays the
  premium API option).

### Three Qwen3-TTS capabilities we use
| Capability | Model variant | Use in WhipRadio |
|---|---|---|
| Generic / cloning | `…-Base` | clone a voice from a ~3 s sample (human imports, "voice matches the song" later) |
| Preset speakers | `…-CustomVoice` | quick stock voices when a host doesn't need a designed voice |
| **Voice design from text** | `…-VoiceDesign` | **the headline:** mint a consistent voice from a natural-language description |

---

## 2. Why Voice-Design changes the architecture (do this right)

Voice-Design lets us **create** a voice from words. This makes the Phase 0 `VoiceProfile`
"by construction" idea concrete and should be brought forward *now*, because it removes a
whole class of bugs (the Herbert-Nachtwelle gender mismatch from Phase 1.5):

- A `VoiceProfile` carries: `Gender`, free-text `Timbre/StyleDescription`, the chosen
  `Engine`, and an engine-specific `VoiceHandle` (for Qwen: the designed-voice
  artifact/seed; for Kokoro: the preset id).
- **Minting a voice:** feed `Gender + StyleDescription` to Qwen Voice-Design → obtain a
  stable voice artifact → persist the handle so the voice is **identical across restarts**
  (determinism is mandatory — store seed/artifact, never re-design on the fly).
- The same `StyleDescription` later conditions the ACE-Step vocal prompt (Phase 5), so a
  member's speaking and singing voices are consistent by construction.

**Firm rule:** a host's voice must be reproducible. Never regenerate a designed voice
per-call; design once, store the handle, reuse.

---

## 3. Emotion / prosody — simplify the marker pipeline

Qwen3-TTS adapts tone, rate, and emotion from **instructions and text semantics**. This
overlaps with the existing speech-marker system (`[pause]`, `[breath]`, `[rate:…]`).

- Add an `instruction` channel to `ITtsEngine.SynthesizeAsync` options (nullable; ignored
  by engines that don't support it).
- The VoiceDirector may now emit a short natural-language delivery instruction
  ("slightly excited, brisk, warm") **in addition to or instead of** bracket markers.
- **Keep the marker normalizer** for engines without instruction support and for hard
  timing needs (`[pause:NNNms]` is still the precise way to place silence). For Qwen,
  prefer instructions for *style*, markers for *hard timing*.
- `UseBreath` (Phase 2 per-host flag) still applies: with Qwen, breaths come from
  instruction/semantics, not the spliced breath sample — when `UseBreath=false`, omit the
  breath cue from the instruction.

---

## 4. Sidecar work (extend `sidecars/tts`)

- New engine module `app/qwen_engine.py` implementing the existing engine contract
  (`/synthesize`, `/voices`) plus two new endpoints:
  - `POST /design-voice` → body `{ description, gender, sample_text? }` → returns a
    persisted `voiceHandle` (+ a short preview WAV). Used at host creation / onboarding.
  - `POST /clone-voice` → body `{ sample_wav, name }` → returns a `voiceHandle`. Used for
    human imports and (later) song-matched voices.
- `/synthesize` accepts `voiceHandle`, `language`, `rate`, and optional `instruction`.
- Model + size from env (`QWEN_TTS_MODEL`, default the 0.6B); models cached in the
  existing `HF_HOME=/models` volume (first start downloads).
- Output: WAV at the canonical rate (resample to 44.1 kHz stereo for the mixer; Qwen can
  emit up to 48 kHz — downsample consistently).
- Keep latency sane: Qwen3-TTS supports streaming; for Phase-3 just return the full WAV
  (announcements are produced ahead), but leave a note for streaming if live mic/Phase 7
  ever needs it.

---

## 5. C# / orchestrator work

- `QwenTtsEngine : ITtsEngine` in Infrastructure (HTTP client to the sidecar), plus a
  small `IVoiceDesignClient` (`DesignVoiceAsync`, `CloneVoiceAsync`) — these are *new*
  capabilities, not part of `ITtsEngine`, so engines that lack them aren't forced to
  implement them.
- `VoiceProfile` entity (or extend Moderator): `Engine`, `Gender`, `StyleDescription`,
  `VoiceHandle`, optional `Fx` (empty; reserved for the deferred telephone effect).
- Host creation / edit UI: choose engine; for Qwen, a **"design voice"** flow — type a
  description, hear a preview, save the handle. For Kokoro/Piper, the existing preset
  picker. Migration path below.
- Settings (technical): TTS engine default, Qwen model size (0.6B/1.7B), resample target.

---

## 6. Migration of existing hosts

- **Do not break currently-working hosts.** They keep their current engine/voice until
  re-assigned. (Multiple engines coexisting is already supported.)
- Provide an admin action **"Redesign voice with Qwen"** per host: uses the host's
  `Gender` + `Style` (+ `PersonaPrompt` excerpt) to mint a Qwen voice, preview, confirm,
  then store the new `VoiceProfile`. One-click upgrade, reversible.
- Seed/default new hosts (and onboarding-created hosts) to Qwen designed voices.
- **Backfill check:** the Phase-1.5 "all hosts same voice" / gender-mismatch bugs should
  be re-verified gone after redesign — two same-gender hosts must get audibly distinct
  designed voices (different `StyleDescription` ⇒ different handle).

---

## 7. VRAM reality (12 GB, update to Phase 0 budget)

| Component | Residency | Rough VRAM |
|---|---|---|
| Gemma 4 E4B (Q4) + ~16–32K KV | resident | ~4–5 GB |
| **Qwen3-TTS 0.6B** | resident | ~1.5–2.5 GB |
| Qwen3-TTS 1.7B (alt) | resident | ~3–4 GB |
| Music (ACE-Step) | on-demand / evictable | remaining |

- With **0.6B**, LLM + TTS resident ≈ 6–7 GB, leaving ~5–6 GB for music when it runs —
  comfortable.
- With **1.7B**, it gets tight; rely on the shared generation semaphore so TTS synthesis
  and music generation don't both spike, and accept music evicting.
- Recommend shipping **0.6B as default**; expose 1.7B for bigger cards.

---

## 8. Milestones (tight, ordered)

### M1 — Sidecar engine
Add `qwen_engine.py`, `/synthesize` with `instruction`, model size via env, resample to
44.1 kHz. Smoke test: German + English synth returns valid WAV; ffprobe duration sane.

### M2 — Voice design & clone endpoints
`/design-voice` + `/clone-voice` returning persistable handles + preview WAV. Determinism
test: same description+seed ⇒ same voice (byte-stable enough that re-synth matches).

### M3 — C# engine + voice-design client + VoiceProfile
`QwenTtsEngine`, `IVoiceDesignClient`, `VoiceProfile` persistence. Unit tests with a fake
handler: synth request shape (incl. instruction), design/clone round-trip, handle stored.

### M4 — UI: design flow + engine selection + settings
Host create/edit "design voice" flow with preview; engine + Qwen model-size settings.

### M5 — Migration + bug re-verification
"Redesign with Qwen" admin action; re-verify gender-correct + distinct voices; existing
hosts keep working until redesigned.

### M6 — Rollout
Default new/onboarding hosts to Qwen 0.6B. Keep Kokoro/Piper/ElevenLabs selectable. Soak:
30-min run, German + English hosts, no stream breakage, loudness consistent into the 3a
mixer.

---

## 9. Definition of Done
- [ ] `dotnet build` + `dotnet test` green (≥ 8 new tests)
- [ ] Qwen3-TTS engine works behind `ITtsEngine`; no orchestrator calling-code changes
- [ ] German host sounds natural (the original weak-German problem is gone)
- [ ] Voice-Design: a host's voice is minted from a text description and is reproducible
      across restarts (stored handle, not regenerated)
- [ ] Two same-gender hosts get audibly distinct designed voices
- [ ] Optional `instruction` channel drives style; `[pause:NNNms]` still places hard
      silence; `UseBreath=false` suppresses breath cues
- [ ] Kokoro/Piper/ElevenLabs remain available as fallback/options
- [ ] 0.6B default fits the 12 GB budget alongside E4B + music (verified)
- [ ] Loudness into the 3a mixer stays consistent (no re-tuning of duck levels needed)

---

## 10. Risks & fallbacks (apply without asking)
| Risk | Fallback |
|---|---|
| 1.7B too heavy with LLM + music on 12 GB | default to 0.6B; serialize via semaphore; let music evict |
| Voice-Design output not deterministic enough | pin seed + cache the produced voice artifact; treat the cached artifact as the source of truth, never re-design per call |
| Qwen German prosody worse than hoped on some text | keep Piper-DE as a per-host fallback; A/B a few hosts before full migration |
| Sidecar model download large/slow on first run | reuse Phase-2 M6 model-download progress + readiness gating; don't start ShowRunner until TTS healthy |
| Instruction channel inconsistent across engines | instruction is nullable and engine-optional; markers remain the portable baseline |
| Existing hosts regress on migration | migration is opt-in per host and reversible; nothing auto-migrates |
