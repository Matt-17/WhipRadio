# WhipRadio — Phase 3c Brief: Rich Content

> Design brief. Firm where it counts, open where the project's shape should decide.
> Builds on 3a (mixer) and 3b (PromptContextBuilder, memory, priorities).
>
> **Theme:** the station gets *real content* — news and traffic from the world,
> multi-voice talk/podcast segments, and the ability to hit the top of the hour.

---

## 1. Goal

Three capabilities: (1) news & traffic announcements through the existing
`IAnnouncementDataSource` abstraction; (2) a **ConversationSegment** engine that
produces multi-speaker talks and podcasts (same machine, different length/structure);
(3) **top-of-the-hour timing** so news lands at :00.

---

## 2. News & Traffic (the easy, high-value part)

The interfaces already exist from Phase 1. This is implementations + scheduling.

- **News:** RSS sources (tagesschau, BBC, configurable list) → fetch top N headlines →
  ScriptWriter summarises into spoken radio copy → TTS. **Firm copyright rule:**
  summarise and rewrite in the host's own words; never read article text verbatim.
- **Traffic:** start with a DE-friendly source. Options: the Autobahn API (no key,
  Germany motorways) or HERE/TomTom (keyed, broader). Recommend Autobahn first for the
  homelab use case; abstract behind `ITrafficSource` so a keyed provider can replace it.
- **Scheduling:** these are `Scheduled` priority announcements (from 3b). News at :00,
  traffic at :20/:50, etc. — all configurable. A dedicated news host is optional
  (mirror the weather specialist pattern from 3b if desired).

**Open Choice:** how much editorial filtering (skip certain categories, dedupe similar
headlines). Recommend a simple per-source headline cap + a recency window; leave
smarter curation as a later refinement.

---

## 3. ConversationSegment engine (talks = podcasts, one machine)

A talk and a podcast are the same artifact at different scales. Model **one** concept:

`ConversationSegment`:
- `Kind` (Talk | Podcast) — really just presets for the fields below
- `Participants` (ordered list of host/guest ids, 2–5)
- `TargetDurationMinutes`
- `Structure` (Freeform | Chaptered) + optional `Chapters[]`
- `Topic` + `Brief` (what it's about)
- `Status` (Planned/Scripted/Produced/Used)
- produced output: a single mixed WAV + a stored transcript

### Production pipeline (the firm part)
1. **Plan** (LLM, reasoning provider): from topic + participants + duration, produce a
   structure — for a podcast, 3–5 chapters with a one-line intent each; for a talk, just
   a beat list. Word budget per chapter derived from each speaker's rate (reuse 3b's
   word-budget math).
2. **Script** the dialogue. **This is where Phase 5's multi-agent choice looms.** In 3c,
   keep it tractable: a single LLM call can generate a *speaker-tagged* script
   (`[CHARLIE]: …` / `[JENNY]: …`). Phase 5 will upgrade this to true per-agent turns
   (Option B). **Design the `ConversationSegment` so that upgrade needs no schema
   change** — store turns as a list of `(speakerId, text, markers)`, however they were
   generated.
3. **Voice** each turn via that speaker's TTS voice (from their `Moderator`/`Artist`
   record).
4. **Assemble** turns into one WAV. With the 3a mixer, turns can slightly overlap for
   natural interruptions (a third source slot). Keep overlaps small and optional in 3c;
   the 5-people-talking-over-each-other vision is Phase 5.
5. **Schedule:** a segment occupies a format slot (a "talk show" / "podcast" format the
   Program Director can place). Long segments need the mixer's lookahead to pre-produce.

**Open Choice:** produce podcasts fully ahead of time (simpler, safe) vs stream-produced
chapter-by-chapter. Recommend produce-ahead in 3c — a podcast is not time-sensitive and
pre-production avoids any live stall.

---

## 4. Top-of-the-hour timing

The user flagged this as desirable-but-luxury. With the 3a mixer it's now reachable
because the mixer already does sample-accurate scheduling.

**Approach (firm enough):** the ShowRunner gains a `TimingPlanner` that, as :00
approaches, looks at the remaining queue and chooses one of:
- pick a *next track whose duration fits* the remaining time to :00 (selection-time
  solution — cheapest, preferred);
- start the crossfade early / extend an outro to land on :00 (mixer already supports
  early fades);
- drop in a short jingle or station-id to fill a small gap;
- as last resort, a clean hard-cut at :00 into the news (radio does this constantly).

**Firm rule:** never time-stretch music to fit (out of scope; sounds bad). Timing is
solved by *selection and fades*, not tempo manipulation.

**Open Choice:** how tight the target is (±2 s vs exact). Recommend ±2 s for 3c; exact
alignment can come if news/traffic prove popular.

---

## 5. Suggested milestone spine (agent refines)
1. News source(s) + ScriptWriter summarisation + scheduled placement.
2. Traffic source (Autobahn first) behind `ITrafficSource`.
3. `ConversationSegment` model + single-call speaker-tagged scripting + assembly.
4. Talk/podcast formats the Program Director can schedule.
5. `TimingPlanner` for top-of-hour, selection-first with fade/jingle fallbacks.

---

## 6. Definition of Done (themes)
- [ ] News announcements: summarised (never verbatim), scheduled, host-voiced
- [ ] Traffic announcements from a DE source, behind a swappable interface
- [ ] A 2-speaker talk and a 3-speaker chaptered podcast both produce a mixed WAV +
      transcript, schedulable as formats
- [ ] `ConversationSegment` stores turns as `(speaker, text, markers)` — ready for
      Phase 5 multi-agent with no schema change
- [ ] News lands within ±2 s of :00 via selection/fades, never via time-stretch
- [ ] Stream stays live throughout; long podcasts pre-produced

---

## 7. Open questions
- News/traffic sources: which exact feeds, and any region beyond Germany at launch?
- Podcast length ceiling for the homelab (production time vs library churn)?
- Should talk/podcast transcripts surface on the existing /playlog + a new content page?
- How aggressive should top-of-hour be before it's worth the complexity?
