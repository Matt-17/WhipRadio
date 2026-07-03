# WhipRadio — Phase 6 Brief: External World

> Design brief. WhipRadio reaches outside itself: real-world facts for real songs,
> a live audience via Twitch, and your own voice via the browser mic. Three fairly
> independent features — they can ship in any order.

---

## 1. Goal

(1) Enrich content with **external knowledge** (Wikipedia & friends) so hosts can talk
about *real* artists/songs with substance, not just invented ones. (2) A **Twitch
connector** that feeds live chat to the on-air host, who *decides* whether to react.
(3) **Browser microphone recording** so you can voice announcements yourself — or run the
station with no TTS at all.

---

## 2. External knowledge enrichment

Until now, artists and songs are invented. Phase 6 lets the station also handle **real**
music and speak about it credibly.

- `IKnowledgeSource` abstraction; first implementation **Wikipedia/Wikidata** (open,
  no key). Given a real artist/track, fetch a summary, formation facts, notable works,
  trivia.
- **Firm copyright rule (carries the project's existing discipline):** hosts *summarise
  and paraphrase* facts; never read article text verbatim, never reproduce lyrics. Store
  a short factual digest, not the source text.
- Cache results in a `KnowledgeEntry` table keyed by entity, with a refresh window, so
  you don't re-fetch every airing and you stay offline-friendly between fetches.
- Feeds `PromptContextBuilder`: a host doing a "Detailed" intro for a real track gets a
  factual digest to riff on.

**Open Choice:** scope of "real music" support — only enrich metadata for an imported
human library (the planned `Backend="library"` tracks), or also let hosts reference real
outside artists conversationally. Recommend starting with enriching the imported library;
broader open-world references are a smaller add once the source exists.

**Open Choice:** additional sources (MusicBrainz for discography, etc.) behind the same
interface. Recommend leaving the interface multi-source from day one.

---

## 3. Twitch connector

A genuinely fun feature: the live audience becomes show material.

- `ITwitchChatSource` connects to a channel's chat (IRC/EventSub). Incoming messages flow
  onto an internal bus (reuse the notification/event plumbing).
- **The host decides.** Messages don't auto-air. The on-air host's agent periodically
  receives a *batch* of recent chat (filtered, rate-limited) as part of its
  `PromptContext` and may choose — via an action like `ReagiereAufChat(msgId)` — to work
  a message into its next talk. Most messages are ignored, as on real radio.
- **Safety/firmness:** hard moderation before anything reaches an agent — profanity/links/
  spam filtering, length caps, per-user rate limits, and an allow/block list. Never let
  raw audience text become spoken output without the host-agent paraphrasing it.
- Surfaces in the Chat page too (a read-only "audience" channel) so you can watch what
  the host is reacting to.

**Open Choice:** read-only (host reacts) vs interactive (audience can request songs/
greetings, tying into Phase 2's greeting system). Recommend read-only first; requests are
a natural follow-on through the existing greeting pipeline.

---

## 4. Browser microphone recording

Two motivations: insert your own voice occasionally, or run TTS-free entirely.

- A recording control in the web app (Live/Branding/Admin as fits) captures mic audio in
  the browser, uploads it, and stores it as an `Announcement` (or `Jingle`) with a
  `Source = "human"` marker. It flows through the same queue/mixer as any other item.
- **Manual mode:** a station/host setting where some or all announcements are expected to
  be human-recorded. The ShowRunner then either waits for / pulls from a pool of your
  recordings, or prompts you (via chat) that a slot needs a recording.
- Reuses the upload hook noted in 3b's jingle section.

**Firm rules:**
- All recording is explicit and user-initiated (clear record/stop, review before
  publish). No background capture.
- Normalise uploaded audio to the canonical PCM/loudness target (3a) so human and TTS
  items sit at the same level.

**Open Choice:** live "go to mic now" (interrupt and speak live) vs record-then-queue.
Recommend record-then-queue first — true live mic injection is a latency/timing project
closer to Phase 7 hardening.

---

## 5. Suggested milestone spine (agent refines)
1. `IKnowledgeSource` + Wikipedia/Wikidata impl + `KnowledgeEntry` cache + builder hook.
2. Enrich the imported human library; "Detailed" intros use digests (paraphrased).
3. `ITwitchChatSource` + moderation pipeline + audience channel (read-only).
4. Host `ReagiereAufChat` action + batched chat in `PromptContext`.
5. Browser mic capture → upload → `Announcement(Source=human)` through mixer.
6. Manual-mode setting + ShowRunner handling of human-recorded slots.

---

## 6. Definition of Done (themes)
- [ ] A real (imported) track gets a paraphrased factual intro from a knowledge source;
      no verbatim text or lyrics ever reproduced
- [ ] Knowledge is cached and the station works offline between fetches
- [ ] Twitch chat flows in, is moderated, and the host *chooses* what to react to
- [ ] No raw audience text is ever spoken without host-agent paraphrasing
- [ ] You can record an announcement in the browser and hear it on air, level-matched
- [ ] Manual mode lets the station run with human recordings instead of TTS

---

## 7. Open questions
- Knowledge scope: imported library only, or open-world references too?
- Twitch: read-only reactions first, or wire audience requests via the greeting system?
- Mic: record-then-queue only, or eventually true live injection (likely Phase 7)?
- Which extra knowledge sources (MusicBrainz, Discogs) are worth the interface effort?
