# WhipRadio - Phase 3c.2 Brief: Podcasts And Conversation Segments

> Follow-up to Phase 3c.1.
> Phase 3c.1 proves single-speaker news and top-of-hour package timing first.
> Phase 3c.2 adds the multi-speaker segment engine.

---

## 1. Goal

Add `ConversationSegment`: a produce-ahead engine for talks and podcasts that stores
speaker turns, transcript, participants, structure, and one mixed WAV for playout.

This is separate from Phase 3c.1 because podcasts need multi-speaker planning,
turn-taking, voice assignment, and longer offline rendering.

---

## 2. Model Shape

`ConversationSegment`:

- `Kind`: Talk or Podcast.
- `Participants`: ordered host, guest, artist, or artist-member speakers.
- `TargetDurationMinutes`.
- `Structure`: Freeform or Chaptered.
- `Chapters`: optional chapter title, intent, and target duration.
- `Topic` and `Brief`.
- `Status`: Planned, Scripted, Produced, Used, Failed.
- `Transcript`.
- `OutputFilePath`.
- `DurationSeconds`.

Turns must be stored as structured rows or JSON with:

- speaker id
- text
- optional speech markers
- optional timing hints

The schema should not require changes when a later phase upgrades from one LLM call to
true multi-agent speaker turns.

---

## 3. Production Pipeline

1. Plan structure from topic, participants, and duration.
2. Generate a speaker-tagged script in one LLM call for Phase 3c.2.
3. Parse and validate turns.
4. Voice each turn using the speaker voice.
5. Assemble one WAV, initially with simple ordered turn spacing.
6. Store transcript and produced output.
7. Schedule as a format item or prepared special segment.

---

## 4. Definition Of Done

- [x] Two-speaker talk produces a transcript and mixed WAV.
- [x] Three-speaker chaptered podcast produces a transcript and mixed WAV.
- [x] Turns are stored as speaker-tagged structured data.
      (`ConversationSegment.TurnsJson` — one `ConversationTurn` record per utterance,
      generation-agnostic for a later multi-agent writer.)
- [x] Produced segments can be scheduled without live-stream stalls.
      (Podcast shows are grid format blocks; episodes land via the multi-slot
      `TimedPlayoutInterruptService`; one-off talks air via queue-front "Air next".)
- [x] Artist members can be selected as future speakers using existing rich artist data.
      (Members without a designed voice are selectable; production enqueues priority
      voice design and waits.)
- [x] The design remains compatible with later multi-agent dialogue generation.
