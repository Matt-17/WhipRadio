# WhipRadio — Project Summary

> *"It really gets whipped by llamas."*
> A self-hosted internet radio station with an LLM program director. Fully local
> except declared external data sources. Currently in beta, fast-built with heavy
> AI assistance, German developer (Code-iX, Dresden), .NET/C# stack, target hardware
> a 12 GB consumer GPU (RTX 4070 class).
>
> This document is the map of everything discussed and planned so far. It does not
> replace the individual phase documents — it tells you which one to open.

---

## 1. The core idea, in one paragraph

The **Program Director** (an LLM) plans a weekly schedule of formats and hosts; the
station plays whatever's in its **record collection**, weighted by votes and play
count, and rotates **hosts** who talk between songs. Everything past "schedule + music
library" is an optional module: spoken announcements, AI-generated music, weather/news,
podcasts, listener chat control. The minimum useful station is just a schedule and a
library. The maximum is a fully autonomous, self-narrating, self-populating AI station.

---

## 2. Positioning (vs. what already exists)

Three existing categories, none of which overlap WhipRadio directly:
- **Classic radio automation** (AzuraCast, LibreTime, OpenBroadcaster) — mature
  scheduler+streamer for human-curated music, **no AI layer**. AzuraCast explicitly
  refuses AI-assisted contributions.
- **Commercial AI-radio products** (Futuri RadioGPT/AudioAI) — cloud, proprietary,
  OpenAI-dependent, built for broadcast companies localizing content, not for self-hosting.
- **Hobby AI-DJ scripts** (WRIT-FM, AI-FM, savonet/ai-radio) — genuinely close in spirit
  (Claude/GPT writes DJ patter, local TTS speaks it, Icecast streams it), but each is a
  **loop script**, not a system: no program director, no host data model with memory, no
  modular studios, no web app with voting/schedule/stats, no container orchestration.

**WhipRadio's niche:** AzuraCast-class self-hosted seriousness, the hobby-project local-AI
spirit, plus a director that plans and adapts instead of a script that just loops.

---

## 3. Architecture at a glance

```
.NET Aspire AppHost
 ├─ Orchestrator (.NET) — ShowRunner, MusicProduction, AnnouncementProduction,
 │                         Playout, AudioMixer, Program Director, Chat agents (later)
 ├─ Web (Blazor)        — player, library, schedule, hosts, admin, stats, chat (later)
 ├─ Ollama               — Gemma 4 E4B (text/reasoning)
 ├─ tts sidecar (Python) — Qwen3-TTS (+ Kokoro/Piper/ElevenLabs as alt engines)
 ├─ music sidecar(Python)— ACE-Step (vocals) + MusicGen (instrumental)
 ├─ image sidecar (later)— FLUX.2 Klein 4B, lowest GPU priority (Phase 6b)
 └─ Icecast              — one MP3 mount, ICY metadata, played by web/VLC/Winamp
SQLite on a persistent volume holds everything: tracks, artists, hosts, formats,
schedule, announcements/talk material, play log, votes, settings.
```

**VRAM doctrine (12 GB target):** LLM + local TTS stay resident for responsiveness.
Music generation is the swapper — loads on demand, allowed to be slow. Image generation
(Phase 6b) is lowest priority and evicts everything else when it runs. A shared
generation semaphore prevents simultaneous spikes. Full detail: `Phase-0-Tech-Decisions.md`.

**Models chosen (June 2026 snapshot):**
| Role | Model | Why |
|---|---|---|
| Text/reasoning | **Gemma 4 E4B** via Ollama | multilingual, Apache 2.0, fits 12 GB with TTS+music |
| Speech | **Qwen3-TTS 0.6B** | voice *design* from text description, 10 languages incl. German, best-in-class speaker similarity |
| Music | **ACE-Step** (vocal) + MusicGen (instrumental) | local, structure-aware |
| Images (later) | **FLUX.2 Klein 4B** | photorealistic, multi-reference identity, Apache 2.0 |
| Internal actions | **self-built `Aktion()` protocol** | MCP explicitly rejected as overkill for an internal loop both ends of which we own |

---

## 4. What's actually running today

**Phase 1 (MVP):** Aspire-orchestrated stack boots; Icecast stream plays in browser/VLC/
Winamp; local music generation (MusicGen) and TTS work; two-stage announcement pipeline
(ScriptWriter → VoiceDirector); multiple moderators in DB; weather via Open-Meteo;
sequential Song→Announcement→Song playout; basic web app (player, library, log,
moderators, settings); manual GitHub Action builds/pushes Docker images.

**Phase 1.5 → Phase 2 (also shipped, from a long live-testing punch list):** persistent
footer player across page nav; SignalR real-time updates (1 s ticking, instant
now-playing); host gender + correct voice assignment; host rotation hard-capped; handover
announcements (farewell/intro); talk-type variety beyond "song intro"; day-memory for
continuity; Artist table with genre/sub-genre taxonomy and dedup-checked titles; variable
track duration (not fixed 1:30); generation throttling; model selection in admin;
±vote bar; ElevenLabs + Piper-DE + OpenAI as alternate providers behind existing
interfaces; per-host breath toggle; admin on-air/start-stop controls; Icecast ICY
metadata for Winamp; console log page; statistics page; configurable frequency;
**listener greetings/requests via the web app** (own milestone, M2.6); IO-abort and
language-mixing bugs fixed. Full detail: `Plan.md` (Phase 1), `Phase2.md` (Phase 1.5+2).

**Phase 3a (in progress):** the **AudioMixerService** — a pure-DSP `MixerCore` (equal-power
crossfades, gain envelopes, sample-accurate scheduling) sitting between decoders and the
Icecast encoder. Per-track analysis (BPM, beat grid, intro/outro, loudness) drives a
**MixPlanner** that picks a transition strategy per item pair. **`HardCut` is first-class**
— the routine choice after talk, not an error fallback. Strategies: HardCut, EnergyFade,
OutroBridgeIn, BeatAlignedFade, IntroTalkOver ("hit the post" — talk ends just before a
song's energy kicks in), OutroTalkOver. Ships behind a flag; legacy sequential playout
stays available. Full detail: `Phase-Crossfade-Plan.md`.

**Emergency insert, decided alongside 3a:** swap TTS to **Qwen3-TTS**, keeping Kokoro/
Piper/ElevenLabs as fallback engines behind the existing `ITtsEngine` interface. Headline
feature: **Voice-Design from a text description**, minted once and stored as a reusable
handle (never re-designed per call) — this is what makes the `VoiceProfile` "by
construction" idea (Phase 0) concrete, and structurally fixes the earlier gender/
duplicate-voice bugs. Full detail: `Phase-3-Emergency-TTS-Plan.md`.

---

## 5. The newest idea, not yet folded into a phase doc: TalkBreak & material lifespan

Discussed live, supersedes part of the 3b announcement-priority section:

- **Two lifespans of spoken material.** *Ephemeral* (greeting, song intro, weather) —
  single-purpose, TTL 24h, then deleted. *Evergreen* (a joke, an anecdote, a bit) — lives
  as long as the host exists, reusable forever.
- **Evergreen material stores the *idea*, not just one rendition.** A `Bit` holds a
  premise plus its produced renditions (text+WAV). Three ways to use it: replay an exact
  WAV (free, and real radio does this constantly — sweepers/drops repeat verbatim); have
  the LLM **re-tell it freshly** from the premise (cheap LLM+TTS pass, varies with mood/
  context); or — not useful — re-render identical text. A rotation mechanic mirrors the
  track-library logic: per-bit cooldown, selection weight falls with play count, forced
  re-telling after N exact replays, eventual retirement. "Goldies, but for talk."
- **Dedup applies here too**, same mechanism as song-title dedup: check new bit premises
  against existing ones before storing, or the LLM reinvents the same joke repeatedly.
- **The playout unit becomes a `TalkBreak`**, not a bare announcement: one queue item
  containing 1–N ordered parts (e.g. comment on the last song + an evergreen anecdote +
  weather + next-song intro), rendered as a single WAV by the same offline
  `SegmentRenderer` planned for Phase 5 multi-speaker segments — a TalkBreak is just a
  one-speaker segment. "Up next" and the play log show one "Announcement" item,
  expandable to its parts in the log.
- **Per-host `TalkProfile`:** break frequency (every track? every third?), min/max parts,
  allowed part kinds. A host who "only wants music" is simply frequency = 0 (or
  station-IDs only). Composes with the Phase 3b `TalkDepth`/`TalkDensity` format
  properties — format sets the envelope, host profile works within it.
- After telling a bit, log a `DayMemory` note so a host doesn't introduce an afternoon
  anecdote as brand-new again at night.

**Status:** agreed in conversation; not yet written into `Phase-3b-Identity-and-Personality.md`
(should replace/extend its §6 announcement-priority section when that doc is next revised).

---

## 6. The full roadmap (phase documents)

| Doc | Phase | What it covers | Status |
|---|---|---|---|
| `Plan.md` | 1 | MVP bootstrap to first audible stream | ✅ shipped |
| `Phase2.md` | 1.5 / 2 | Live-testing fixes, real-time UI, Program Director v1, formats/schedule, ElevenLabs/OpenAI, stats, greetings | ✅ shipped |
| `Phase-Crossfade-Plan.md` | 3a | AudioMixerService, analysis pipeline, MixPlanner | 🔧 in progress |
| `Phase-3-Emergency-TTS-Plan.md` | 3 (insert) | Qwen3-TTS adoption, Voice-Design, migration | 🔧 next |
| `Phase-3b-Identity-and-Personality.md` | 3b | PromptContextBuilder (word-budget math), memory layers, mood drift, format talk-depth, branding page, weather fix + specialist host | 📝 design brief |
| `Phase-3c-Rich-Content.md` | 3c | News/traffic sources, ConversationSegment (talks=podcasts), top-of-hour timing | 📝 design brief |
| `Phase-4-Chat-Control.md` | 4 | Chat page, `Aktion()` action protocol, host/director permissions, host-to-host (Option B, real multi-agent), system notifications | 📝 design brief |
| `Phase-5-Artists-and-Guests.md` | 5 | Rich `Artist`/`BandMember` model, `VoiceProfile`, guests as chat entities, `ConversationDirector` + `SegmentRenderer`, group talks | 📝 design brief |
| `Phase-6-External-World.md` | 6 | Wikipedia/knowledge enrichment, Twitch chat (host decides what to react to), browser mic recording | 📝 design brief |
| `Phase-6b-Photography.md` | 6b | FLUX.2 Klein 4B band/host portraits, lowest-priority queue, member-photos-before-group-photo dependency | 📝 design brief |
| `Phase-7-Onboarding-and-Deployment.md` | 7 | Chat-driven director-led onboarding (CEO/director in one), Docker Compose hardening, optional Kubernetes/Helm | 📝 design brief |
| `Phase-8-Business-RSaaS.md` | 8 | Tenancy model, GPU cost economics, OSS/hosted boundary — strategy sketch only | 💭 sketch |
| `Phase-0-Tech-Decisions.md` | — | Cross-cutting, binding decisions: hardware target, model choices, VRAM budget, MCP rejection, audio-reuse doctrine, voice-consistency doctrine | 🔒 locked, supersedes conflicts |

**Document style note:** Phase 1/2/3a/Emergency-TTS are written prescriptively (step-by-
step, for direct agent execution). Phase 3b onward are **design briefs** — firm on
data-shape decisions, explicitly open on implementation specifics the project's shape
should decide later.

---

## 7. Standing rules that apply across every phase
- Never break the live stream; risky subsystems ship behind flags.
- `dotnet build` + `dotnet test` stay green at every milestone.
- Copyright discipline: paraphrase external facts, never reproduce article text or lyrics.
- No skip button — deliberate design constraint, not a missing feature.
- Everything local except declared external sources (weather now; news/traffic/Twitch/
  OpenAI/ElevenLabs later, each off by default and behind a swappable interface).
- MCP is rejected for the internal control loop; the self-built `Aktion()` protocol stays.
- Voice and visual identity are achieved "by construction" (shared `VoiceProfile`/
  biography driving multiple generators), not by post-hoc matching across engines.

---

## 8. Also exists: the project website

`whipradio-website.html` — single-file landing page. Signature element: an FM tuner dial
that *is* the navigation (frequencies = section anchors), scroll-aware (random scan above
the fold, locks to the current chapter's frequency once you scroll into it, header
frequency stays in sync, digits tween like a digital tuner). Content was deliberately
rewritten from "AI feature showcase" to sober, factual copy aimed at the actual first
audience: local-AI-curious nerds who want to know what leaves the network, what hardware
is needed, which models are used and whether they're swappable, where data lives, and an
honest beta-status admission (fast, AI-assisted build; rough edges expected). Hardware
claims are kept generic (no specific GPU model named) since the user's 4070 is a dev
target, not a requirement.

---

## 9. Open threads / not yet decided
- TalkBreak/Bit lifecycle (§5 above) needs writing into `Phase-3b...md`.
- Telephone/lo-fi caller voice effect — designed for (`VoiceFx` field reserved) but not
  built; planned alongside guests in Phase 5/6.
- Group cross-talk/overlap timing — `ConversationSegment` turns store a `timing?` field
  for this; first implementation is sequential turns only, overlap is a later refinement.
- Onboarding UX: pure chat vs. hybrid with structured inputs — leaning hybrid, not fixed.
- Phase 8 tenancy model (single-tenant-per-deploy vs multi-tenant) — flagged as the
  expensive-to-reverse decision, intentionally deferred.
