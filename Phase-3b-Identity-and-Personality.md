# WhipRadio — Phase 3b Brief: Identity, Personality & Memory

> **Style of this document:** a design brief, not a rigid step list. It states firm
> decisions where they matter (data shape, the prompt-context contract) and leaves
> **Open Choices** where the right answer depends on how earlier phases turned out.
> The agent should propose its concrete milestone breakdown before coding, using the
> structure here as the spine.
>
> **Theme:** make hosts feel like *people* and make the station feel like a *brand*.
> Nothing here touches the audio path — Phase 3a owns that.
>
> **Status:** Phase 3b is implemented. Historical preflight findings, milestone
> outcomes, and verification notes are retained in this brief so it is the single
> source for the phase.

---

## 1. Goal

After 3b, every host has: a stable character, layered memory, a mood that drifts
slowly (never lurches), and — crucially — **hard situational facts injected into every
prompt** so the host can reason about time and length. Spoken segments become
organic `TalkBreaks` made from purposeful parts, including reusable host-owned bits
that can be replayed or freshly retold. The station gets a Branding identity (name,
slogan, vision/mission, jingles) on its own page, ordinary station defaults stay in
Settings, and the Settings/Admin surfaces are reorganised. Weather becomes time-aware
with a dedicated weather host.

---

## 2. The PromptContextBuilder (the backbone — build this first)

Every LLM call in the system (ScriptWriter, VoiceDirector, Program Director, later the
chat agents) must be assembled by **one** component so facts are consistent and never
forgotten. This is the most important deliverable of 3b.

`IPromptContextBuilder.Build(PromptScope scope) → PromptContext`

A `PromptContext` carries, at minimum:
- **Station facts:** name, frequency, slogan, vision/mission, current local server time
  (with day of week).
- **Format facts:** active format name, its purpose, its required talk-depth, remaining
  minutes in the current slot, what comes after.
- **Host facts:** persona summary, language, **speech rate**, and the derived
  **word budget** — `wordsPerSecond = baseWordsPerSec × speechRate` (calibrate
  `baseWordsPerSec` per language; ~2.5 wps German, ~2.8 wps English as starting points,
  refine from measured TTS output durations stored in `Announcement.DurationSeconds`).
- **Time-on-air math:** "you have N seconds before the next scheduled item; that's
  roughly M words at your speaking rate." This single sentence is what lets a host
  decide between a one-liner and a five-minute ramble.
- **Continuity:** the relevant memory slices (see §3).
- **Recent station history:** recently played songs, previous track, next track
  (title/artist/genre if known), recent talk topics, jokes, recurring bits, and
  deduplication hints so the host does not repeat itself or call an old topic new.
- **Current purpose:** why this prompt exists now (song intro, weather, evergreen bit,
  emergency, station id, listener message, handover, etc.) and how important it is.

**Firm rule:** no service constructs prompts ad hoc anymore. If a service needs a fact
that isn't in `PromptContext`, the fact gets added to the builder, not hand-glued into
one prompt. This is what makes Phase 4's chat agents tractable.

**Open Choice:** whether `PromptContext` renders to a single system-prompt string or to
a structured set of messages (system + a "situation" message). Recommend the latter —
it survives provider swaps (Ollama vs OpenAI) better. Agent decides based on how the
Phase 2 provider factory ended up shaped.

---

## 2a. Character tool-call contract

Every character-facing LLM result should be treated as a **tool-call plan**, not as raw
free text. Hosts, the Program Director, guests, artists, and later chat/user-facing
characters are all "thinking characters": they reason from `PromptContext`, then choose
explicit actions from the tools made available to that prompt.

Examples:

- `Announce("text to announce")`
- `Play("music title or track id")`
- `Message(CharacterId, "message")`
- `StartTalkBreak([...parts])`
- `Remember("short memory note")`
- `RequestBit("premise or desired theme")`

Phase 3b does not need to depend on provider-native tool calling. The local contract can
start as structured JSON/text parsed by the app, then later map to OpenAI/Ollama native
tool APIs where useful.

Implementation shape:

- Define tool-call classes/records in code, each with name, description, arguments,
  validation, and optional execution handler.
- Register or discover the available tools at startup.
- A `CharacterToolCatalog` renders the allowed tool list into `PromptContext`.
- Each `PromptScope` exposes only the tools that make sense for that character and
  situation. A weather host should not get the same tools as the Program Director.
- No character service should interpret arbitrary prose as an action. It should parse
  and validate tool calls, reject invalid calls, and fall back safely.

This gives organic behavior without making prompts ambiguous: a host can decide whether
to announce, play, remember, message the Program Director, summarize a show, or stay
quiet, but the system still receives explicit machine-readable intent.

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

`DayMemory` is also updated after a reusable `TalkBit` is played or retold. Store a
plain summary of the premise and the angle used today, not only the transcript, so later
breaks know the host already used that anecdote/joke.

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

Talk behavior is a **Format** property primarily, layered with host style. Song-intro
detail is only one visible case.

- Add to `Format`: `TalkDepth` (enum: NameOnly, Light, Detailed, DeepDive) and
  `TalkDensity` (how often the host speaks between songs).
- Add a per-host `TalkProfile`: break frequency, min/max parts per break, allowed
  part kinds, evergreen-bit tolerance, exact-replay tolerance, and whether this host is
  effectively music-only. A host that "only wants music" is not special-cased; their
  profile simply has frequency 0 or allows only station IDs.
- ScriptWriter consumes `TalkDepth` from `PromptContext`:
  - NameOnly → "just say what it was / what's next, one line."
  - Detailed/DeepDive → "give background, a story, context about the track."
- The host's `Talkativeness` trait modulates *within* the format's band — a chatty host
  in a NameOnly format still keeps it short, just slightly warmer.
- The `TalkProfile` works inside the format's boundaries. The format says how much
  talk belongs in the show; the host profile says what that host naturally does with
  that room.

This cleanly resolves "some hosts only say the name, others go deep" without per-host
hardcoding: the same host adapts to the show they're on.

---

## 6. TalkBreaks, reusable Bits & priority production

Replace the mental model "one announcement equals one playout item" with
`TalkBreak`: a single playout item made from one or more ordered parts, rendered to one
WAV. Examples:

- `[previous-song comment] + [evergreen anecdote] + [weather] + [next-song intro]`
- `[listener greeting] + [short station id]`
- `[emergency message]`

The public queue and now-playing surface may still show "Announcement", but logs/admin
views should be able to expand the break into its parts. This is the small version of
the later SegmentRenderer: TalkBreak = mini segment, podcast = large segment, same
machine over time.

### Talk parts

Each part has `Kind`, `Purpose`, `Priority`, `TargetWindow`, optional related entities
(track, listener message, weather report, jingle, reusable bit), and a desired duration
or word budget. Short-lived parts such as weather, greetings, song intros and dedications
are purpose-bound and get TTL cleanup. A 24h TTL is the default unless the part has a
stricter target window.

### Reusable Bits

Long-lived host material lives as a `TalkBit`: a premise plus produced renditions.
The premise is the durable thing ("the joke about the drummer and the metronome"); each
rendition is a concrete text/WAV produced from that premise.

There are only two useful reuse modes:

1. **Exact replay:** reuse the existing WAV for free. This is valid radio behavior for
   sweepers, drops, station IDs and occasional recurring gags.
2. **Fresh retelling:** feed the premise into the LLM with current `PromptContext` so the
   host retells it in today's mood, time, format, and surrounding music context.

Do not implement "same text, new render" as a normal mode; it costs TTS and adds little.

Use the same selection logic as the music library:

- Cooldown per bit, e.g. minimum 5 days before reuse.
- Selection weight falls as play count rises.
- After N exact replays, force a fresh retelling.
- Retire stale bits eventually.
- When generating new bits, check against existing premises with the same keyword
  exclusion/dedup spirit used for song titles so the LLM does not invent the fifth
  version of the same joke.

After any bit is told, write a short summary to `DayMemory`. Without that note, a host
can accidentally present the 14:00 anecdote as brand-new at 19:00, which breaks the
illusion of a person with continuity.

### Priorities

Priority belongs to parts and breaks, but the scheduling unit is the `TalkBreak`.

| Priority | Scheduling behaviour |
|---|---|
| `Emergency` | interrupt: duck/await current item, insert within ~15 s |
| `High` | jump to front of queue, before the next planned item |
| `Normal` | normal break planning |
| `Low` | only when queue depth < 2 and the host/format profile allows it |
| `Scheduled(at)` | produced ahead, played within a tolerance window of a target time |

**Produce-ahead vs just-in-time:** predictable breaks can be planned and rendered ahead
(scheduled weather, station IDs, prepared evergreen bits), but Emergency/High always have
an on-demand path with their own LLM/TTS budget. A full produce-ahead pool must never
block an emergency break.

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

**Resolved default:** use a short hand-in before the report, then quick-cut back after
the weather. Example: the main host says "Here is {WeatherHostName} with the weather",
the specialist gives the report, and the next regular item follows without a generated
return handover.

Consequence: this keeps the station feeling staffed and intentional without adding the
latency and stale-context risk of a post-weather mini-handover. A fuller handover can
remain a later config option if TalkBreak timing proves stable.

---

## 8. Branding page & surface reorganisation

Split the current overloaded surfaces into three clear pages:

- **Branding** (new): station name, slogan (distinct from the WhipRadio product slogan),
  station vision/mission, logo/colours (nice-to-have), and the
  **Jingle library**. These facts feed `PromptContext`, TalkBreak planning and the
  Program Director.
- **Settings**: ordinary station defaults and technical controls - station language,
  display frequency, first day of week, API keys, providers, GPU/context, timeouts,
  generation intervals.
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

## 9. Implementation Results

Phase 3b was completed as eight buildable milestones:

1. `PromptContextBuilder` was added with prompt scopes, station/format/host facts, recent station history, memory slices, current purpose, priority, and word-budget math. ScriptWriter, VoiceDirector, Program Director, MessageModerator, and HostLanguageAligner now use the shared context path. `MusicCopywriter` remains outside the host/station prompt contract until music copy gets an explicit character/tool scope.
2. Character tool-call contracts were added with startup-discovered tool definitions, prompt rendering, parser/validator support, and safe fallback behavior. Initial tools include `Announce`, `Play`, `Message`, `StartTalkBreak`, `Remember`, `RequestBit`, and `NoOp`.
3. Memory layers were implemented with `MemoryLayer`, layer-aware read/write helpers, FIFO guards, DayMemory writes after talk, and nightly distillation into LongTermMemory.
4. Personality traits and mood drift were implemented with ordinal trait enums, baseline/static values, current variable mood values, an hourly `MoodEngine`, and VoiceDirector prompt support.
5. Format-driven talk depth and host `TalkProfile` were implemented. Talk planning now combines format boundaries with host preferences, and ScriptWriter instructions scale song intros from name-only to deep-dive.
6. `TalkBreak`, `TalkPart`, `TalkBit`, and `TalkBitRendition` were added. Spoken content can be modeled as 1-N ordered parts, collapsed in public now-playing, expanded in logs/admin, cleaned up by TTL, replayed exactly where allowed, or freshly retold from premise. `SegmentRenderer` renders true ordered multi-part breaks into one WAV, and bit usage writes DayMemory.
7. Weather was reworked into structured `WeatherReport` facts with current-hour temperature, tonight low, tomorrow outlook, optional multi-day glance, weather specialist host support, cadence settings, and TalkBreak-based hand-in/report flow.
8. Branding and jingles were added: station slogan/vision/mission, Branding page, jingle table/library, ACE-Step jingle generation, jingle TalkBreak use, and a cleaner split where station language/frequency/week remain Settings.

## 10. Completed Acceptance Criteria

- [x] Every host/station LLM prompt in the system is assembled by `PromptContextBuilder`.
- [x] Character-facing LLM results are parsed as validated tool calls, not arbitrary free text.
- [x] Startup-registered tool definitions are rendered into each prompt scope.
- [x] Hosts can adjust talk length to remaining slot time; logs include word budget, produced words, available seconds, and rendered duration.
- [x] Mood drift is unit-tested, moves at most one step/hour, and stays within +/-2 of baseline.
- [x] The same host can produce NameOnly vs Detailed intros depending on format.
- [x] TalkBreaks can contain 1-N ordered parts and render to one WAV/playout item.
- [x] Reusable TalkBits support exact replay and fresh retelling, with cooldown, weighting, dedup, forced retelling, and retirement.
- [x] DayMemory is updated after a bit is played or retold.
- [x] Emergency/High TalkBreak production has an on-demand path independent of any produce-ahead pool.
- [x] Evening weather uses current-hour temperature instead of daily max and includes tomorrow/multi-day context through the weather specialist.
- [x] Branding drives station facts into prompts; jingles are generated through ACE-Step.
- [x] Settings (ordinary defaults and technical controls), Admin (operations), and Branding (identity) are separated.

## 11. Retained Preflight Notes And Resolved Decisions

Important discovery findings retained from preflight:

- The original direct LLM prompt sites were `ScriptWriter`, `VoiceDirector`, `MessageModerator`, `ProgramDirectorService` day-plan/persona generation, `MusicCopywriter`, and `HostLanguageAligner`.
- Pre-existing model anchors included `Moderator.SpeechRate`, `Moderator.Talkativeness`, `Format.Talkativeness`, `StationSettings.StationName`, `StationSettings.FrequencyMhz`, `ModeratorMemory`, and `ShowContext`.
- The main gaps before implementation were missing station slogan/vision/mission, untyped memory, missing `TalkDepth`, missing host `TalkProfile`, single-item announcements, string-only weather, and `ShowContext` losing slot timing.
- AppHost dashboard output alone is not sufficient runtime evidence. When the UI has no data, inspect Orchestrator/Web endpoints and Aspire/DCP resource logs.
- EF migration discovery must be verified with tooling. Hand-written migration files need normal EF metadata attributes, otherwise the live app can query schema that EF never applies.

Resolved defaults:

- `PromptContext` stays structured internally and renders to the current `systemPrompt`/`userPrompt` interface until provider contracts are widened.
- Character tools use a provider-neutral JSON/text envelope first; provider-native tool calling can be an adapter later.
- Memory extends the existing `ModeratorMemory` table with a `MemoryLayer`.
- Initial word budgets use constants: German ~2.5 wps, English ~2.8 wps, with later calibration from real TTS durations.
- Mood drift is autonomous in Phase 3b. Overview/Admin shows baseline properties and current variable mood values, with constant properties visually distinct from changing ones.
- Exact replay is allowed for station IDs, jingles, drops, and recurring jokes. Ordinary anecdotes should usually be freshly retold rather than replayed exactly.
- Initial TalkBit defaults: 5-day cooldown, 2 exact replays before forced retelling, then eventual retirement by age/play count.
- Now Playing stays collapsed as 0 or 1 "Announcement" TalkBreak; PlayLog/Admin can expand the parts.
- Jingle upload UI is deferred unless it becomes clearly necessary.
- Weather specialist flow: main host gives a short hand-in before the weather report; after the report, quick cut back to regular programming by default.

## 12. Verification Record

- Preflight baseline passed `dotnet build WhipRadio.slnx` and `dotnet test WhipRadio.slnx` with 196 tests before Phase 3b edits.
- Final Phase 3b verification passed `dotnet test .\WhipRadio.slnx --no-build` with 240 tests total: Core 165, Infrastructure 72, Orchestrator 3.
- EF migration discovery now lists all six Phase 3b migrations: `Phase3bMemoryLayers`, `Phase3bPersonalityTraits`, `Phase3bTalkProfiles`, `Phase3bTalkBreaksAndBits`, `Phase3bWeather`, and `Phase3bBrandingAndJingles`.
- Runtime verification after the migration fix: AppHost/Web/Orchestrator/DCP stay running, `/api/console` returns data, `/api/library` returns tracks, `/console` returns HTTP 200, and browser hydration shows console lines with no reconnect modal or Blazor error UI.
- Known environment note: `dotnet run --project .\src\WhipRadio.AppHost --no-build --no-restore` is the reliable AppHost launch path after the app code is built.
