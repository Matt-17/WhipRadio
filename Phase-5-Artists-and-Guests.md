# WhipRadio — Phase 5 Brief: Artists & Guests

> Design brief. This phase deepens *who* populates the station and lets up to five of
> them hold a real conversation — on air and in chat. Leans on Phase 4's action protocol
> and 3c's `ConversationSegment`.

---

## 1. Goal

Artists become real entities with substantial biographies (members, names, genders,
ages, interests), good enough to sustain a multi-person podcast. Artists and guests
become **chat participants** you can invite and direct. The multi-agent loop from Phase 4
grows into **true group conversations** (2–5 speakers) that can overlap naturally on air
using the 3a mixer.

---

## 2. Rich artist/band model (the foundation)

Today's artist is a thin name. Make it a populated entity:

`Artist` gains: `Type` (SoloAct | Band | Duo…), `FormationYear?`, `Origin?`,
`Backstory`, `Interests[]`, plus a child collection:

`BandMember`: `Name, Gender, Age?, Role` (vocals, guitar, production…), `Personality`
(reuse 3b trait enums where sensible), `Interests[]`, and a `VoiceProfile` (so a member
can *speak* in a podcast).

**`VoiceProfile` (firm — see Phase 0):** `Gender`, coarse timbre descriptors, a resolved
TTS `VoiceId`, and an optional `Fx` chain (empty for now; reserved for the telephone/
lo-fi caller effect that lands later). The **same** `VoiceProfile` drives both the TTS
speaking voice and the ACE-Step vocal prompt, so a member's singing and speaking voices
are consistent *by construction* rather than matched post-hoc. True cross-engine cloning
(identical sung/spoken voice) is a stretch goal, not a requirement. A solo singer-
songwriter or a duo is just a band with one or two members — each still needs a
`VoiceProfile`.

**Generation:** when the Program Director or a host "discovers" (creates) an artist, the
LLM fills this whole structure, not just a name — so a later 5-person talk has actual
people with actual opinions. Store the `GenerationPrompt`/seed for reproducibility.

**Firm rule:** a band that appears in a talk must have enough members with voices to fill
the speaker count; the producer validates this before scheduling a multi-voice segment.

**Open Choice:** how much of this is visible/editable in the UI (an Artist master/detail
page with members) vs purely LLM-managed. Recommend a read-mostly Artist page with the
member roster surfaced — it's also great for the "is this a good band" judgement, mirror
of Phase 2's host preview.

---

## 3. Guests & artists as chat entities

Generalise Phase 4's chat participants. A host, an artist, a band member, or a one-off
guest are all **ChatParticipants** with a role and a `PromptContext` recipe.

Flows the user named:
- Invite an artist/guest into a channel ("lade Artist X ein").
- Ask them to make a new song ("mach doch mal einen Indie-Track") → triggers music
  generation attributed to that artist, with their style.
- Brief a podcast ("redet über Songs A, B, C") → those tracks are referenced in the
  segment *and* scheduled to play around it.

**Firm rule:** guest/artist actions use the same permissioned action catalogue (Phase 4)
— a guest has a narrow verb set (talk, agree, suggest a song) and cannot, say, re-plan
the week.

---

## 4. True group conversations (the headline feature)

Upgrade `ConversationSegment` scripting from "one call, speaker-tagged" to **real
per-agent turns** (Option B at group scale):

- A `ConversationDirector` orchestrates: it holds the segment brief, tracks whose turn it
  is, and calls **each participant's own agent** with that participant's `PromptContext`
  (their persona, memory, interests, and the running transcript so far).
- Each agent returns its next contribution; the director appends it and decides who
  speaks next (round-robin, or "addressed-to" detection, or a light social model).
- **Rendering — offline, via `SegmentRenderer` (firm; see Phase 0):** the segment is
  premixed into a *single WAV* at production time by a `SegmentRenderer` that reuses the
  pure `MixerCore` from Phase 3a. The live `AudioMixerService` is **not** involved — a
  finished talk/podcast is one item to the live system. This makes turn timing fully
  deterministic and keeps the live path simple.
- **Cross-talk / overlap (deferred refinement):** because turns are separate audio, the
  `SegmentRenderer` *can* overlap a speaker's tail with the next speaker's head for
  natural cross-talk. **In the first cut, keep turns sequential** (clean, no overlap) —
  this already delivers a believable multi-person talk. Rich overlap is a later
  refinement, and it's genuinely hard: it needs the LLM to mark *what overlaps when* in
  the chapter plan and still sound natural. Store turns as
  `(speakerId, text, markers, timing?)` so overlap timing can be added with no schema
  change when you tackle it.

**Firm guardrails (carry from Phase 4, scale up):**
- Max turns per segment; max overlap; a terminal condition (chapters exhausted /
  duration budget hit, from 3b word-budget math).
- Cost/time awareness: 5 agents × many turns is many LLM calls — the producer must
  estimate and stay within the segment's production budget, degrading to fewer turns or a
  single-call fallback if needed.

**Open Choice:** turn-taking policy (strict round-robin vs addressed-to vs a tiny
"who wants to speak" scorer). Recommend starting addressed-to + round-robin fallback;
make the policy pluggable (`ITurnTakingPolicy`) so it can evolve.

---

## 5. Memory upgrade (optional but fitting here)

Group conversations expose the limits of flat-text memory. This is the natural place to
introduce **retrieval** (the Phase 3b note): embed talk summaries / artist facts and let
the `PromptContextBuilder` pull the most relevant slices per participant. Keep it local
(a small embedding model + SQLite vector extension or an in-process index).

**Open Choice:** do this now or defer. Recommend a *thin* version now (retrieve top-k
past summaries for a participant) because 5-way talks otherwise repeat themselves.

---

## 6. Suggested milestone spine (agent refines)
1. Rich `Artist` + `BandMember` model + LLM population + Artist master/detail page.
2. Generalise chat participants to include artists/band members/guests + narrow verb set.
3. Invite/request flows (new song, brief a podcast with specific tracks).
4. `ConversationDirector` + per-agent turns (Option B) + `ITurnTakingPolicy`.
5. `SegmentRenderer` (reuses `MixerCore`) → premixed single WAV, **sequential turns
   first**; bounded overlap kept as a parameterised, deferred refinement.
6. Production budgeting + degradation fallback.
7. (Optional) thin retrieval memory for participants.

---

## 7. Definition of Done (themes)
- [ ] Artists have members with names, genders, ages, roles, interests, voices
- [ ] A 5-person band can hold a chaptered podcast where each member speaks in-character
- [ ] Turns are generated per-agent (Option B), each with its own context/memory
- [ ] A talk/podcast is premixed by `SegmentRenderer` into one WAV (reusing `MixerCore`)
      and plays as a single live item; turns sequential and believable
- [ ] Natural bounded cross-talk is possible later via `timing?` with no schema change
- [ ] Inviting an artist in chat and asking for a song produces a track in their style
- [ ] Briefing a podcast on specific tracks references *and* schedules those tracks
- [ ] Production stays within a per-segment time/cost budget, with a safe fallback

---

## 8. Open questions
- How autonomous are artists (do they "live" between appearances, accruing memory)?
- Turn-taking policy default and how social/realistic to make it.
- Retrieval memory now (recommended thin) vs Phase 6.
- UI depth for artists — full management or mostly read-only roster.
