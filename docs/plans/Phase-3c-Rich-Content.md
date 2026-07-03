# WhipRadio - Phase 3c Brief: News And Top-Of-Hour Packages

> Design brief. This is now Phase 3c.1.
> Traffic is moved to Phase 9. Podcasts and multi-speaker conversation segments are
> moved to Phase 3c.2. This phase focuses on single-speaker news production and a
> timing planner that can land larger spoken packages at the top of the hour.
>
> Product constraint: WhipRadio is mandatory international software. Defaults,
> examples, prompts, and seeded sources should be English-first, with US/global news
> as the primary baseline because the product is tech-oriented. Region-specific
> sources must be user-configurable, not baked in as the default experience.

---

## 1. Goal

Phase 3c adds real editorial programming without taking on the full podcast engine yet:

1. **News production:** fetch RSS headlines, select useful items, rewrite them into
   original radio copy, voice them through the existing host/TTS pipeline, and store
   them as scheduled TalkBreak parts.
2. **Top-of-hour package planning:** prepare and air a package at :00 that can contain
   station ID, news, weather, and later traffic. In Phase 3c.1, traffic is only a
   reserved placeholder for Phase 9.
3. **News formats:** support short top-of-hour news packages and longer scheduled
   news shows, such as an 8 AM or 8 PM 30-minute news format.

News is deliberately simpler than podcasts: one presenter, one topic at a time, no
multi-speaker turn engine. That makes it the right first content system before Phase
3c.2 introduces ConversationSegment.

---

## 2. Non-Goals For This Phase

- No traffic provider implementation. Traffic belongs to Phase 9.
- No multi-speaker podcasts or panel talks. Those belong to Phase 3c.2.
- No article text read verbatim on air.
- No time-stretching music to land a package at :00.
- No region-specific hardcoded default that makes WhipRadio feel local-only.

---

## 3. News Pipeline

### Source model

Use RSS as the first news source because it is international, keyless, common, and
simple to test.

Initial source shape:

- `NewsFeed`: label, URL, language, region tag, category, enabled flag, poll cadence,
  max items per poll.
- Default feeds: English US/global technology and world/general feeds.
- User feeds: operators can add national, regional, or niche feeds later through
  Settings/Admin.
- Each fetched item stores enough metadata to dedupe and audit: title, URL, source,
  published time, summary/description if present, content hash, first seen time,
  status, and selected/not-selected reason.

### Selection and rewrite

The LLM should select from headline/teaser metadata first. Full article extraction is
optional and should be a later refinement unless RSS summaries prove too thin.

Rules:

- Summarize and rewrite in WhipRadio's own words.
- Never read article text verbatim.
- Prefer useful, current, high-signal stories over filler.
- Dedupe near-identical headlines across feeds.
- Keep source attribution factual and brief.
- Store generated script/transcript and source metadata for audit.

### Spoken output

Add news as a first-class announcement/talk part:

- `AnnouncementKind.News`
- `TalkPartKind.News`
- `NewsBrief` or equivalent persisted source/selection model
- `ScriptWriter.News` prompt template
- host or dedicated news presenter, resolved through settings

The output should be ordinary produced announcement WAVs so existing TalkBreak,
SegmentRenderer, play log, and now-playing behavior can be reused.

---

## 4. Top-Of-Hour Package Planning

This is the most important Phase 3c capability.

A top-of-hour package is not just weather. It is a planned, timed spoken package that
can contain:

- station ID or jingle
- news headlines or a longer news block
- weather
- traffic placeholder, implemented in Phase 9
- short return or transition into music

The package should be produced ahead of time, then landed at :00 within an initial
tolerance of about +/-2 seconds.

### Package durations

Support at least two package classes:

- **Short hourly package:** usually 60 seconds to 5 minutes.
- **Long news format:** up to 30 minutes, scheduled as a format block such as morning
  or evening news.

The short package is a TalkBreak package. The long news format may be a sequence of
news items and music beds/jingles, but it should still use the same source selection,
script, and timing primitives where practical.

### TimingPlanner

The ShowRunner gains a `TimingPlanner` that looks at the current time, active item,
queue depth, known durations, scheduled packages, and available jingles.

Preferred strategy order:

1. Pick the next track whose duration fits the remaining time before :00.
2. If the gap is small, use a station ID/jingle/fill talk to bridge it.
3. If a fitting top-of-hour intro/handoff is ready, it may start within the
   configured grace window.
4. If live audio is still active, fade it out with the configured package fade
   duration (default: 1 second) before starting the package.

Firm rule: never time-stretch music. Timing is solved with selection, fades, fills,
jingles, and short package fade-outs.

---

## 5. Suggested Milestone Spine

1. Rescope docs and settings defaults around English-first international behavior.
2. Add the news domain model and EF migration.
3. Add RSS polling, feed validation, dedupe, and tests.
4. Add news selection/rewrite prompt and `AnnouncementKind.News`.
5. Add short top-of-hour package production: news + weather + station ID/jingle.
6. Add `TimingPlanner` with duration-aware track selection and fill fallback.
7. Add long scheduled news format support for blocks up to 30 minutes.
8. Add operator UI/API for feeds, package cadence, news presenter, and package logs.

---

## 6. Definition Of Done

- [ ] News feeds can be configured, polled, deduped, and audited.
- [ ] News scripts are rewritten original radio copy, never article text playback.
- [ ] News can be voiced by a host or configured news presenter.
- [ ] Top-of-hour package can include station ID/jingle, news, weather, and a Phase 9
      traffic placeholder.
- [ ] Short packages can run up to 5 minutes without stalling the live stream.
- [ ] A scheduled news format can run as a longer block, target 30 minutes.
- [ ] TimingPlanner lands the package at :00 within about +/-2 seconds when feasible.
- [ ] TimingPlanner never time-stretches music.
- [ ] If exact timing is impossible, the system chooses a clean fallback and logs why.
- [ ] Everything remains English-first by default and configurable for international
      deployments.

---

## 7. Open Questions

- Which English US/global RSS feeds should be seeded by default?
- Should full article extraction be Phase 3c.1 or deferred until RSS summaries prove
  insufficient?
- Should a dedicated news presenter be seeded, or should the current host read news by
  default?
- Should long news formats include music beds/jingles in Phase 3c.1, or stay spoken
  first?
- How visible should source attribution and generated transcripts be in the UI?
