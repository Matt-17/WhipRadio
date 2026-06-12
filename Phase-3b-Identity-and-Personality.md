# WhipRadio — Phase 3b Brief: Identity, Personality & Memory

> **Style of this document:** a design brief, not a rigid step list. It states firm
> decisions where they matter (data shape, the prompt-context contract) and leaves
> **Open Choices** where the right answer depends on how earlier phases turned out.
> The agent should propose its concrete milestone breakdown before coding, using the
> structure here as the spine.
>
> **Theme:** make hosts feel like *people* and make the station feel like a *brand*.
> Nothing here touches the audio path — Phase 3a owns that.

---

## 1. Goal

After 3b, every host has: a stable character, layered memory, a mood that drifts
slowly (never lurches), and — crucially — **hard situational facts injected into every
prompt** so the host can reason about time and length. The station gets a Branding
identity (name, frequency, slogan, jingles) on its own page, and the Settings/Admin
surfaces are reorganised. Weather becomes time-aware with a dedicated weather host.

---

## 2. The PromptContextBuilder (the backbone — build this first)

Every LLM call in the system (ScriptWriter, VoiceDirector, Program Director, later the
chat agents) must be assembled by **one** component so facts are consistent and never
forgotten. This is the most important deliverable of 3b.

`IPromptContextBuilder.Build(PromptScope scope) → PromptContext`

A `PromptContext` carries, at minimum:
- **Station facts:** name, frequency, slogan, current local server time (with day of week).
- **Format facts:** active format name, its required talk-depth, remaining minutes in
  the current slot, what comes after.
- **Host facts:** persona summary, language, **speech rate**, and the derived
  **word budget** — `wordsPerSecond = baseWordsPerSec × speechRate` (calibrate
  `baseWordsPerSec` per language; ~2.5 wps German, ~2.8 wps English as starting points,
  refine from measured TTS output durations stored in `Announcement.DurationSeconds`).
- **Time-on-air math:** "you have N seconds before the next scheduled item; that's
  roughly M words at your speaking rate." This single sentence is what lets a host
  decide between a one-liner and a five-minute ramble.
- **Continuity:** the relevant memory slices (see §3).
- **Now/next:** previous track, next track (title/artist/genre if known), recent talk
  topics (so the host doesn't repeat itself).

**Firm rule:** no service constructs prompts ad hoc anymore. If a service needs a fact
that isn't in `PromptContext`, the fact gets added to the builder, not hand-glued into
one prompt. This is what makes Phase 4's chat agents tractable.

**Open Choice:** whether `PromptContext` renders to a single system-prompt string or to
a structured set of messages (system + a "situation" message). Recommend the latter —
it survives provider swaps (Ollama vs OpenAI) better. Agent decides based on how the
Phase 2 provider factory ended up shaped.

---

## 3. Memory layers

Replace the single `DayMemory` blob with explicit layers. Each is just text/JSON the
builder can pull from; no vector DB required in 3b (note it as a Phase 5/6 upgrade).

| Layer | Lifetime | Source | Size guard |
|---|---|---|---|
| `ImmutablePersona` | permanent | host creation / Program Director | — |
| `FormatContext` | per slot | current Format | — |
| `DayMemory` | reset 00:00 | FIFO of talk summaries | ~2 000 chars |
| `LongTermMemory` | weeks | nightly distilled summary of the day | ~3 000 chars |
| `CurrentContext` | per call | built fresh each time | — |

Nightly job (reuse the Program Director's 03:00 slot or a sibling service): distil the
day's `DayMemory` into one or two sentences appended to `LongTermMemory`, FIFO-trimmed.
This is how a host can later say "last week I mentioned…" without unbounded growth.

**Open Choice:** store memory layers as columns on `Moderator` (simple) vs a
`ModeratorMemory` table keyed by layer (cleaner, enables Phase 5 retrieval). Recommend
the table if Phase 5 is firmly on the roadmap (it is).

---

## 4. Personality traits & mood drift

The user's concern is correct: a host must not flip character. Model personality as
**several independent, fine-grained enums**, not one `Mood`:

- `Energy` (VeryLow…VeryHigh), `Formality`, `HumorLevel`, `Talkativeness`,
  `Warmth` — each a small ordinal enum.
- `ImmutablePersona` fixes the *baseline* of each trait.
- A `MoodEngine` applies a **bounded daily drift**: at most one ordinal step per trait
  per hour, biased by time of day (calmer late night, livelier drive-time) plus a small
  seeded random walk. Hard clamp: never more than ±2 steps from the persona baseline.

The VoiceDirector reads the *current* trait values from `PromptContext`; small changes
barely register acoustically, which is exactly the desired subtlety.

**Open Choice:** whether drift is continuous (recomputed each call) or stepped (updated
on a timer). Recommend stepped hourly — easier to reason about and log.

---

## 5. Format-driven talk depth

Song-intro detail is a **Format** property primarily, layered with host style.

- Add to `Format`: `TalkDepth` (enum: NameOnly, Light, Detailed, DeepDive) and
  `TalkDensity` (how often the host speaks between songs).
- ScriptWriter consumes `TalkDepth` from `PromptContext`:
  - NameOnly → "just say what it was / what's next, one line."
  - Detailed/DeepDive → "give background, a story, context about the track."
- The host's `Talkativeness` trait modulates *within* the format's band — a chatty host
  in a NameOnly format still keeps it short, just slightly warmer.

This cleanly resolves "some hosts only say the name, others go deep" without per-host
hardcoding: the same host adapts to the show they're on.

---

## 6. Announcement priorities & just-in-time production

Now that chat (Phase 4) will inject spontaneous announcements, priority and timing need
real structure. Introduce a priority enum and a production strategy:

| Priority | Scheduling behaviour |
|---|---|
| `Emergency` | interrupt: duck/await current item, insert within ~15 s |
| `High` | jump to front of queue, before the next planned item |
| `Normal` | normal queue position |
| `Low` | only when queue depth < 2 |
| `Scheduled(at)` | produced ahead, played within a tolerance window of a target time |

**Produce-ahead vs just-in-time:** the AnnouncementProductionService should keep a small
pool produced *ahead* for predictable items (next track intro, scheduled weather) but
must be able to produce *on demand* for Emergency/High. Add `PlannedAnnouncement` with
`Status (Pending/Produced/Used/Expired)` and a `TargetWindow`. Pre-computed host
scheduling (the Program Director's plan for Friday night) lets the service start
producing Charlie-style intros hours early.

**Firm rule:** an Emergency item must never wait on a full produce-ahead pool — there is
always an on-demand path with its own TTS budget.

---

## 7. Weather (fix + dedicated host)

Two problems to solve:
1. **Time-awareness:** stop reporting the daily max in the evening. Pull the *current
   hour* temperature from Open-Meteo's `hourly` series, plus tonight's low, tomorrow's
   outlook, and an optional 3-day glance. Build a small `WeatherReport` model so the
   ScriptWriter gets structured facts, not a pre-baked sentence.
2. **Dedicated weather host:** a `Moderator` flagged `IsWeatherSpecialist` with its own
   voice/persona. The Program Director (or a setting) can schedule weather hits (e.g.
   top of each hour) spoken by this host regardless of who's on air. All toggle-able in
   Settings.

**Open Choice:** does the weather host briefly "hand over" to the main host after the
report (mini-handover from Phase 2) or just cut back? Recommend a config flag, default
to a quick cut for now; the handover machinery already exists if you want the polish.

---

## 8. Branding page & surface reorganisation

Split the current overloaded surfaces into three clear pages:

- **Branding** (new): station name, frequency (MHz), slogan (distinct from the WhipRadio
  product slogan), logo/colours (nice-to-have), and the **Jingle library**. These facts
  feed `PromptContext` and the Program Director.
- **Settings**: technical only — API keys, providers, GPU/context, timeouts, generation
  intervals. Nothing that defines the station's identity or daily operation.
- **Admin**: live operation — on-air, start/stop services, console, director "run now".
  Consider merging the old Settings' operational bits here.

### Jingles
Generate jingles with the **existing ACE-Step backend** (no second container): short
(5–15 s) station idents from a dedicated prompt, optionally with a sung station name.
Store in a `Jingle` table (`Id, Name, FilePath, Kind, DurationSeconds, IsActive`) and
let the mixer treat a jingle as just another short source (Phase 3a's source model makes
this a drop-in). Jingles are selectable per format/daypart later.

**Open Choice:** allow uploading jingles too (ties into Phase 6's browser-mic / upload).
Recommend leaving an upload hook but not building the UI until Phase 6.

---

## 9. Suggested milestone spine (agent refines)

1. `PromptContextBuilder` + word-budget math + wire *all* existing LLM calls through it.
   (Everything else depends on this.)
2. Memory layers + nightly distillation.
3. Personality traits + `MoodEngine` drift, VoiceDirector reads traits.
4. Format `TalkDepth`/`TalkDensity` + ScriptWriter changes.
5. Announcement priorities + `PlannedAnnouncement` + produce-ahead/on-demand split.
6. Weather rework + weather specialist host.
7. Branding page + Jingle generation/table + Settings/Admin reorg.

Each milestone keeps `dotnet build`/`dotnet test` green and the stream live.

---

## 10. Definition of Done (acceptance themes)
- [ ] Every LLM prompt in the system is assembled by `PromptContextBuilder`
- [ ] A host visibly adjusts talk length to remaining slot time (observable in logs:
      word budget vs produced length)
- [ ] Mood drifts at most one step/hour and stays within ±2 of baseline (unit-tested)
- [ ] Same host produces NameOnly vs Detailed intros depending on format
- [ ] Emergency announcement interrupts within ~15 s without waiting on the pool
- [ ] Evening weather reports the *current* temperature, not the daily max; tomorrow +
      3-day outlook present; spoken by the weather specialist
- [ ] Branding page drives station facts into prompts; jingles generated via ACE-Step
- [ ] Settings (technical) / Admin (ops) / Branding (identity) cleanly separated

---

## 11. Open questions to resolve with the user before/at coding time
- Word-rate calibration: measure from real TTS output or trust the per-language constant?
- Memory as columns vs `ModeratorMemory` table (recommend table).
- Should the Mood engine ever be *visible/editable* in Admin, or fully autonomous?
- Jingle uploads now or deferred to Phase 6.
