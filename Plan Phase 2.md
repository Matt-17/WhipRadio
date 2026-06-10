# WhipRadio — Phase 2 Plan: "It Really Gets Whipped by Llamas"

> **Audience:** AI coding agent (Claude Fable 5 / Copilot).
> Phase 1 is running. Phase 2 fixes all known issues from live testing and adds the
> Program Director, Formats, ElevenLabs/OpenAI integrations, Statistics, and
> real-time UI via SignalR. Execute milestones in order. Each milestone ends with
> Acceptance Criteria. `dotnet build` + `dotnet test` must stay green throughout.
>
> **Rule:** never break the running stream. Every milestone must leave the station
> audible when the AppHost is running.

---

## Categorised Issue Index

The following items from the live-testing feedback are addressed in the milestones
below. Each item is tagged with the milestone(s) that resolve it.

| # | Issue (short) | Milestone |
|---|---|---|
| 1 | Live player stops on page nav → persistent footer player | M1 |
| 2 | Info updates too slow (5 s polling) → SignalR real-time | M1 |
| 3 | Progress bar misunderstood / ugly → track position bar | M1 |
| 4 | Likes/dislikes not updating in live view / footer | M1 |
| 5 | Stream URL just text; no copy button; no Winamp deeplink | M1 |
| 6 | Server local time in header | M1 |
| 7 | Talk transcripts on live page | M1 |
| 8 | Play log: show transcripts, track length, upcoming song | M1 |
| 9 | Play log: allow replaying talks from last hour | M1 |
| 10 | Herbert has female voice; gender not in host list/creation | M2 |
| 11 | Host rotation broken — same host for hours | M2 |
| 12 | Hosts must introduce themselves / say bye on handover | M2 |
| 13 | "Song intro" too uniform — vary talk type naturally | M2 |
| 14 | Host day-memory for natural continuity | M2 |
| 15 | Create host page + listen to recent host talks | M2 |
| 16 | Format page (Program Director output, next airtime, host) | M3 |
| 17 | Weekly schedule / program page (Mon–Sun, 0–24h) | M3 |
| 18 | AI Program Director (Gemma/reasoning) plans formats | M3 |
| 19 | Formats linked to hosts; disable format not host | M3 |
| 20 | Disabling a format triggers future re-planning | M3 |
| 21 | Current format shown prominently on live page | M3 |
| 22 | Artist table + master/detail; style drives selection | M4 |
| 23 | Song title repetition (ghost, neon, echo…) | M4 |
| 24 | Song title/artist dedup check before insert | M4 |
| 25 | Genre + subgenre (techno, trance, d&b, dubstep…) | M4 |
| 26 | Music length 1:30 only → variable 3–7 min | M4 |
| 27 | Music generation rate too fast → throttle, configurable | M4 |
| 28 | Music quality / model selection in admin | M4 |
| 29 | Vocal music generation via alternate engine | M4 |
| 30 | Engine name saved per track | M4 |
| 31 | Votes display as coloured ±bar with total | M4 |
| 32 | Library: sort by title/artist, filter by artist/genre, no auto-rotation | M4 |
| 33 | Breath marker option per host (on/off) | M5 |
| 34 | ElevenLabs TTS integration + voice creation per host | M5 |
| 35 | ElevenLabs API key in settings; enable/disable toggle | M5 |
| 36 | Additional local TTS with German voice (Piper DE) | M5 |
| 37 | OpenAI / ChatGPT as text generation provider | M5 |
| 38 | Provider selection per use-case (text, lyrics, reasoning) | M5 |
| 39 | Icecast stream metadata (now-playing for Winamp) | M6 |
| 40 | Administration page: start/stop on-air, music gen, announce gen | M6 |
| 41 | Console output page (replace sidebar console) | M6 |
| 42 | Model download on first run with progress + GPU selection | M6 |
| 43 | GPU offload + context settings (lightweight) | M6 |
| 44 | Statistics page (nerdy) | M7 |
| 45 | Configurable station frequency (MHz) in settings | M7 |
| 46 | Music per format (format drives genre/style context) | M7 |
| 47 | IO-abort errors after 10 songs (thread cancellation) | M8 (bug) |
| 48 | Mixed language (DE/EN) in host talks | M8 (bug) |
| 49 | All hosts same voice | M8 (bug) |
| 50 | Electronic titles too samey across all songs | M4+M8 |

---

## Data Model Additions

Extend existing EF Core entities. Add new migration `Phase2`.

### Artist *(new table)*
| Field | Type | Notes |
|---|---|---|
| Id | Guid PK | |
| Name | string | unique, case-insensitive |
| Genre | string | primary genre |
| SubGenre | string? | e.g. "Techno", "Drum and Bass" |
| StyleDescription | string | prompt fragment used at generation |
| TotalPlays | int | denormalised for perf |
| UpVotes / DownVotes | int | aggregate of all tracks |
| IsRetired | bool | retire rule: same as Track |
| CreatedAt | DateTime | |

Link `Track.ArtistId (FK, nullable for legacy rows)`.

### Moderator — add fields
| New Field | Type | Notes |
|---|---|---|
| Gender | enum: Male/Female/Neutral | drives voice selection |
| UseBreath | bool | default true; set false to suppress [breath] markers |
| TtsProvider | string | "kokoro" \| "piper-de" \| "elevenlabs" |
| ElevenLabsVoiceId | string? | stored after voice creation |
| DayMemory | string? | JSON blob, reset at midnight; max 2 000 chars |
| LastIntroducedAt | DateTime? | for handover logic |
| FormatId | int? FK | preferred format; null = any |

### Format *(new table)*
| Field | Type | Notes |
|---|---|---|
| Id | int PK | |
| Name | string | "Morning Drive", "Friday Night Party", … |
| Description | string | visible on Format page |
| Genre | string | primary genre for music selection |
| SubGenre | string? | narrows selection further |
| DurationMinutes | int | 30–240 |
| HostId | int? FK | assigned moderator; null = TBD |
| IsActive | bool | |
| UpVotes / DownVotes | int | |
| DirectorReason | string? | reasoning text from Program Director |
| LastReviewedAt | DateTime? | |

### ScheduleSlot — extend
Add `FormatId (FK nullable)`, `DayOfWeek (0=Sun…6=Sat, -1=all)`, `Label ("still in planning")`.

### StationSettings — add fields
`FrequencyMhz (double, default 98.5)`, `WeekStartsOnSunday (bool)`,
`TextProvider ("ollama"\|"openai")`, `LyricsProvider`, `ReasoningProvider`,
`OpenAiApiKey (string?)`, `ElevenLabsApiKey (string?)`, `ElevenLabsEnabled (bool)`,
`OpenAiEnabled (bool)`, `MusicGenerationEnabled (bool)`, `AnnouncementEnabled (bool)`,
`OnAir (bool)`, `GpuLayerCount (int, default -1 = auto)`, `OllamaContextSize (int, default 2048)`,
`MusicGenerationIntervalSeconds (int, default 300)`,
`MaxUnplayedTracksBeforeSlowing (int, default 8)`,
`DefaultMusicDurationSeconds (int, default 240)`,
`MusicDurationVarianceSeconds (int, default 90)`.

### ProgramDirectorLog *(new)*
`Id, CreatedAt, ReasoningText, ResultJson, TriggeredBy (string)`

---

## M1 — Real-time UI & Persistent Footer Player

**Goal:** Navigation never stops audio; all live info updates instantly.

### 1.1 Persistent footer player
- Move `<audio>` element into the main layout (`MainLayout.razor`) — one instance for the entire app lifetime.
- Footer bar (always visible, ~64 px): station logo, **Now Playing** (track/announcement title + host name), elapsed `mm:ss` / total `mm:ss` progress bar (green fill, correct semantics — replaces the confusing red bar), 👍/👎 buttons (disabled during announcement), stream copy button.
- The dedicated Live page becomes a **detail view** only — it embeds the same `NowPlayingState` but does not own the audio.
- On mobile: footer collapses to icon + title + controls.

### 1.2 SignalR hub
- Add `RadioHub : Hub` to Web project. Methods:
  - `BroadcastNowPlaying(NowPlayingDto)` — called by Orchestrator via injected `IHubContext<RadioHub>` whenever item changes.
  - `BroadcastProgress(ProgressDto { ItemId, ElapsedSeconds, TotalSeconds })` — called every **1 second** from PlayoutService via a 1 s `PeriodicTimer`.
  - `BroadcastVote(VoteUpdateDto { TrackId, UpVotes, DownVotes })` — called after each vote write.
  - `BroadcastTranscript(TranscriptDto { AnnouncementId, Text, Host })` — called when an announcement starts playing.
- Blazor pages subscribe on `OnInitializedAsync`, unsubscribe on `Dispose`.
- Polling fallback: keep a 30 s polling timer as safety net only (for reconnect).

### 1.3 Live page additions
- Scrolling transcript panel below the player: last 5 announcements with host name, timestamp, full `VoicedText`.
- Upcoming track chip: show next queued track title + artist if known.
- Current Format badge (name + remaining time in slot).

### 1.4 Play log additions
- Column: Duration (formatted `m:ss`).
- Column: Upcoming indicator (next item already queued).
- Expandable row: click announcement row → show full `VoicedText` transcript inline.
- Inline `<audio>` for announcements from last 60 min: small ▶ button serving `/api/audio/announcements/{id}` (range-request capable static file endpoint in Orchestrator).

### 1.5 Stream URL / copy button
- Remove "for Winamp / VLC" text. Replace with: stream URL in a read-only input + 📋 copy-to-clipboard button (JS interop). No program name references unless a verified deep-link URI scheme is used.

### 1.6 Server time in header
- Inject `TimeProvider` (or `IHostEnvironment`). Display server local time `HH:mm:ss` in the nav header, updated via SignalR `BroadcastProgress` (piggyback server UTC timestamp, convert in JS/Blazor using `TimeZoneInfo`).

**Accept:**
- Navigate between pages → audio never stops.
- Elapsed seconds tick every 1 s in footer.
- Voting in footer updates vote bar on Library page without refresh.
- Transcript appears on live page within 1 s of announcement starting.
- Stream copy button works; no program names in URL area.

---

## M2 — Host Quality & Handover

**Goal:** Hosts sound right (gender/voice), rotate correctly, and vary their speech.

### 2.1 Gender field + voice assignment
- Add `Gender` to Moderator entity (migration).
- In `DbInitializer` seed: assign correct gender to existing hosts; map to gender-appropriate voice IDs per TTS engine (document a small lookup table in code: `KokoroVoiceMap { "male": ["am_adam","am_michael"], "female": ["af_heart","af_sky"] }`).
- Host creation form: required Gender dropdown; auto-suggests compatible voices on selection.
- VoiceDirector prompt: include `Moderator.Gender` so LLM uses correct pronouns.

### 2.2 Host rotation — ShowRunner fix
- ShowRunnerService currently re-checks slot-to-host every loop but may cache stale moderator. Fix: re-query active moderator from DB (or invalidate cache) on every slot boundary.
- When format changes on the hour: log "slot boundary crossed, switching host".
- Hard guard: if same moderator played for > `MaxSameHostMinutes` (config, default 90), force a rotation to another active moderator even mid-slot.

### 2.3 Host handover announcements
- New `AnnouncementKind`: `HostHandover`.
- When ShowRunner detects a host change (outgoing ≠ incoming):
  1. Generate farewell for outgoing host: `ScriptWriter` prompt: *"You are {outgoing.Name}, signing off. Brief, warm, in character. Mention the incoming host {incoming.Name} by name."*
  2. Generate intro for incoming host: *"You are {incoming.Name}, starting your shift. Introduce yourself briefly. Optionally react to what {outgoing.Name} just said."*
  3. Queue: Farewell WAV → Intro WAV → first track.
- Update `Moderator.LastIntroducedAt` to avoid re-introducing within 4 h.

### 2.4 Talk variety — announcement type diversity
- Remove the label "Song Intro" from everywhere visible.
- Introduce a weighted random `TalkKind` selector (configurable weights, stored in `StationSettings` as JSON):

| Kind | Default weight | Trigger condition |
|---|---|---|
| `SongIntro` | 20 | next track known; host hasn't played it before |
| `PostSong` | 15 | previous track known; host didn't mention it before |
| `Banter` | 20 | no condition |
| `DayMemoryNote` | 10 | `DayMemory` non-empty |
| `Weather` | 10 | every 4th cycle |
| `StationId` | 10 | every 6th cycle |
| `HostPersonalStory` | 15 | random |

- For `PostSong`: ScriptWriter gets *"The song that just played was {title} by {artist}. The host did NOT mention the title before playing it. Write a natural post-song comment. Sometimes just say 'das war …'."*
- For `DayMemoryNote`: ScriptWriter gets the serialised DayMemory context (max 800 chars).
- After every talk, append a 1-sentence summary to `Moderator.DayMemory` (LLM: *"Summarise in one sentence what {name} just talked about, third person."*). Trim to 2 000 chars FIFO. Reset at midnight.

### 2.5 Host page — create + preview
- `/hosts` list: add **Create Host** button → modal/page with fields: Name, Gender, Language, VoiceId (dropdown from `ITtsEngine.GetVoicesAsync`), SpeechRate, Style, PersonaPrompt, PreferredGenres, PrefersVocals, TtsProvider, UseBreath.
- Each host row: **▶ Preview** button → fetches `/api/hosts/{id}/recent-talks` (last 5 announcements) → plays inline `<audio>` for each with transcript text shown below. No generate-on-demand in Phase 2 (use stored WAVs only).

**Accept:**
- Herbert Nachtwelle (or any male-named host) gets a male voice after migration/reseed.
- Gender field visible in host list and creation form.
- After 90 min same host, rotation occurs automatically.
- Handover plays farewell + intro between shifts.
- Talk types vary across 10 consecutive announcements (no two consecutive SongIntros if other types are available).
- Host create form functional; recent talks playable.

---

## M3 — Program Director & Schedule

**Goal:** A structured weekly schedule, AI-planned formats, visible on a Schedule page.

### 3.1 Data model (already defined above — apply migration)

### 3.2 Program Director service
`ProgramDirectorService` — a `BackgroundService` that runs:
- **On startup** (if < 70% of week slots have a `FormatId`): run full planning.
- **Daily at 03:00**: re-evaluate next 7 days; only change slots rated poorly (Format.DownVotes > 3 × UpVotes AND DownVotes ≥ 5) or "still in planning" slots.
- **On demand** via Admin API `POST /api/admin/director/run`.

Planning algorithm:
1. Load current week's `ScheduleSlot`s.
2. Load all `Format`s, all active `Moderator`s, all genres/subgenres.
3. Build a JSON context object (slots, formats, hosts, vote summaries, day of week).
4. Call **reasoning LLM** (provider from `StationSettings.ReasoningProvider`):
   - System: *"You are the program director of {StationName}. Plan a compelling weekly schedule. Weekday 06-09 = morning drive (pop/indie). 21-23 Fri/Sat = party (techno/trance). Fill remaining slots with variety. Output ONLY valid JSON matching the provided schema."*
   - Output schema: `[{ "slotId": int, "formatId": int|null, "hostId": int|null, "reason": string }]`
5. Validate JSON, apply updates, log to `ProgramDirectorLog`.
6. For any slot where the director wants a NEW host, create a stub Moderator with LLM-generated Name/Persona (mark `IsActive=false` until TTS voice verified by admin).

**Special day rules** (hard-coded defaults, overridable via schedule settings):
- Friday 20:00–02:00 Sat: subgenre weight +50% for Techno/Trance/House.
- Weekdays 06:00–09:00: Pop/Indie.
- Weekdays 22:00–00:00: Chillout/Ambient.

### 3.3 Schedule page `/schedule`
- Grid: rows = hours 00–23, columns = Monday–Sunday (or Sunday–Saturday per `WeekStartsOnSunday` setting).
- Cell content: Format name, Host name, genre badge. "Still in planning" shown in muted style if `FormatId` is null.
- Colour-coded by genre family (rock=blue, electronic=purple, pop=green, ambient=teal).
- Click cell: side panel with Format detail, DirectorReason, UpVotes/DownVotes, **Disable Format** button.
- **Disable Format**: marks `Format.IsActive=false`, clears `ScheduleSlot.FormatId` (slot becomes "still in planning"), schedules a director re-run in 2 h (not immediately — anti-accident delay).

### 3.4 Format page `/formats`
- List all formats: name, genre/subgenre, duration, current host, next scheduled airtime, vote bar (same ±bar as tracks), DirectorReason, IsActive toggle.
- **Edit** button: change host, genre, duration (admin override of director decision).
- Click format → detail: full reasoning text, history of schedule slots, associated tracks played.

### 3.5 Current format on Live page
- Large badge under station name: "▶ MORNING DRIVE — Indie Rock · ends 09:00".
- Feed via SignalR `BroadcastNowPlaying`.

**Accept:**
- AppHost start → director runs, all 7×24 = 168 slots get a Format within 5 min (or "still in planning" for stragglers).
- Schedule page renders full grid without JS errors.
- Disable Format → slot goes to "still in planning"; director re-run queued.
- Current format visible on live page with correct name.

---

## M4 — Music Library Quality & Artist System

**Goal:** Richer music metadata, no repetitive titles, variable length, proper genre taxonomy.

### 4.1 Artist table + Track linkage
- Create `Artist` table (migration).
- New track generation always resolves an artist first:
  1. LLM: *"Invent a unique {genre} {subgenre} artist name. Output ONLY the name. Avoid generic words like Ghost, Neon, Echo, Static, Fade, Shadow."*
  2. Check `Artists` table (case-insensitive). If exists: reuse (probability weighted by artist UpVotes). If not: insert new.
  3. Generate track title: *"Invent a {genre} song title by artist {artistName}. Must not contain any of these words: {recentTitleWords}. Output ONLY the title."* — `recentTitleWords` = top-50 words from last 30 track titles (prevents repetition).
  4. Dedup check: if `(ArtistId, Title)` already exists, retry title generation up to 3×; on 3rd failure, skip this generation cycle.

### 4.2 Genre + SubGenre taxonomy
- Define a static `GenreTaxonomy` class (in Core):
```
Rock → Classic Rock, Indie Rock, Punk, Metal, Grunge
Electronic → Techno, Trance, House, Drum and Bass, Dubstep, Ambient
Pop → Synthpop, Dream Pop, Indie Pop
Jazz → Smooth Jazz, Bebop, Fusion
Hip-Hop → Lo-fi, Boom Bap, Trap
```
- `ScheduleSlot` and `Format` both carry `Genre` + `SubGenre`.
- Music generation prompt uses both: *"Generate a {DurationSeconds}s {Genre} track, specifically {SubGenre} subgenre. Artist: {ArtistName}."*
- Add `SubGenre` column to Library table; filter dropdown splits Genre → SubGenre.

### 4.3 Variable music duration
- Calculate duration per track: `base = StationSettings.DefaultMusicDurationSeconds` (config default 240 s), `variance = StationSettings.MusicDurationVarianceSeconds` (default 90 s). Actual = `base + Random.Shared.Next(-variance, +variance)`. Clamp 120–420 s.
- Pass `duration_seconds` to music sidecar.
- Update MusicGen sidecar: honour `duration_seconds` properly (MusicGen `melody` model supports up to ~30 s natively; for longer tracks use chunked generation with overlap crossfade in the Python sidecar — overlap 2 s, append). ACE-Step natively supports longer.

### 4.4 Music generation throttle
- `MusicProductionService` checks: if `unplayed tracks >= MaxUnplayedTracksBeforeSlowing` → sleep `MusicGenerationIntervalSeconds` (default 300 s = 5 min) before next generation.
- Add admin toggle: `MusicGenerationEnabled` (from settings). When false: service loops sleeping, no generation.

### 4.5 Model selection in admin
- Settings page: **Music Models** section. Show available backends (`/health` from music sidecar). Per backend: dropdown of available models (MusicGen: small/medium/large; ACE-Step: default). Persist selection in `StationSettings` (new fields `MusicGenModel`, `AceStepModel`).
- Music sidecar: accept `model` field in `/generate` body.

### 4.6 Vote bar
- Replace thumbs count text with a horizontal bar: neutral = 0 = centred; net positive = green fill right; net negative = red fill left. Width proportional to `|net| / (totalVotes + 10)`. Show `totalVotes` as plain number next to bar.
- Every new Track seeds with 10 neutral virtual votes (`UpVotes=5, DownVotes=5`) so the bar starts centred.

### 4.7 Library UI improvements
- Server-side sort: Title (A-Z/Z-A), Artist (A-Z/Z-A), Plays (high/low), Net Votes.
- Filter: Artist dropdown, Genre dropdown, SubGenre dropdown (cascades from Genre).
- Auto-rotation toggle: removed from UI (never auto-rotate; selection is always weighted-random as per plan).
- Artist master/detail: click artist name → `/library/artist/{id}` showing artist info, all tracks, aggregate vote bar, StyleDescription, retire status.

**Accept:**
- 20 tracks: no two share a title; words like "Ghost", "Neon", "Echo" not dominant (may appear but not in majority).
- Track durations vary between 2–7 min (verify with ffprobe in a manual check).
- With `MaxUnplayedTracksBeforeSlowing=5` and 8 unplayed tracks: no new generation starts.
- Vote bar renders correctly; neutral track shows centred grey bar.
- Library filter by SubGenre "Techno" returns only techno tracks.

---

## M5 — Multi-Provider TTS & Text Generation

**Goal:** ElevenLabs, Piper-DE, OpenAI available behind existing interfaces.

### 5.1 Piper DE local TTS
- Add `piper-de` engine to TTS sidecar (`sidecars/tts/app/piper_engine.py`).
- Use `piper-tts` Python package; ship `de_DE-thorsten-high.onnx` model (downloaded on container start if not cached, same `HF_HOME` volume).
- `GET /voices` returns piper-de voices with `language: "de"`.
- `UseBreath=false` for Piper by default (quality too low for breath samples).
- C# `HttpTtsEngine` already selects engine by `TtsVoiceOptions`; no changes needed there.

### 5.2 ElevenLabs TTS
- New `ElevenLabsTtsEngine : ITtsEngine` in `LlamaRadio.Infrastructure`.
- `SynthesizeAsync`: POST to `https://api.xi.io/v1/text-to-speech/{voiceId}` with stability/similarity settings from host config.
- `GetVoicesAsync`: GET `https://api.xi.io/v1/voices` — returns library voices.
- **Voice creation** (`CreateVoiceAsync(string name, string description)` — extra method on a `IElevenLabsTtsClient` interface): POST to `/v1/voices/add` with a short generated sample script (TTS of the sample using a default voice, converted to an uploaded voice — this is advanced; Phase 2 ships just selecting from existing ElevenLabs voice library, not custom voice cloning).
- Host creation form: if `TtsProvider=elevenlabs` → show ElevenLabs voice picker (fetches voices from API if key set).
- Store `ElevenLabsVoiceId` in Moderator row.

### 5.3 Settings: API keys + enable/disable
- Settings page new section **External Providers**:
  - ElevenLabs: toggle + API key input (masked). On save: validate key by calling `GET /voices`. Show success/error.
  - OpenAI: toggle + API key input. On save: validate with a tiny `POST /v1/chat/completions` test call.
- Keys saved to `StationSettings` (encrypted at rest is out of scope for Phase 2; just store in DB with a TODO comment).
- All infrastructure services check `StationSettings.ElevenLabsEnabled` / `OpenAiEnabled` before making calls; if disabled, fall back to local.

### 5.4 OpenAI / ChatGPT text provider
- `OpenAiTextGenerationService : ITextGenerationService` — POST `https://api.openai.com/v1/chat/completions`, model `gpt-4o-mini` (cheapest capable model; configurable).
- `ITextGenerationServiceFactory` (new): takes `StationSettings.TextProvider` / `LyricsProvider` / `ReasoningProvider` → returns the right implementation. Registered as the default resolution strategy.
- Both `ScriptWriter` and `VoiceDirector` resolve via factory.
- Program Director uses `ReasoningProvider`.

### 5.5 Breath per-host option
- `VoiceDirector`: if `Moderator.UseBreath == false` → strip all `[breath]` markers from output (post-process before handing to TTS).
- Settings default: `UseBreath=true` for Kokoro, `false` for Piper-DE (enforce in initializer).

**Accept:**
- Create a German-language host with Piper-DE voice; announcement plays in German with no breath artifacts.
- ElevenLabs toggle disabled by default; entering a valid key enables it; hosts can be assigned ElevenLabs voices.
- OpenAI toggle: when enabled, ScriptWriter calls `api.openai.com` (verify via log/Aspire trace).
- UseBreath=false host: no `[breath]` in `VoicedText` DB column.

---

## M6 — Administration, Stream Metadata & Console

### 6.1 Admin page `/admin`
Sections:
- **On Air**: big toggle (maps to `StationSettings.OnAir`). When off: PlayoutService emits silence (a looped 5-s silence WAV) to Icecast instead of content. Station is still technically streaming, just silence.
- **Music Generation**: start/stop toggle + current status (generating / idle / throttled / disabled). Shows estimated time to next generation.
- **Announcement Generation**: start/stop toggle + status.
- **Program Director**: "Run Now" button → `POST /api/admin/director/run`; shows last run timestamp and result summary.
- **Model Management**: list models (Ollama: `GET /api/tags`; music sidecar: `GET /health`). Download status (progress bar via SignalR). GPU layer count slider (0 = CPU only, -1 = auto). Ollama context size input.

### 6.2 Model download on first run
- OrchestratorStartup (in `IHostedService.StartAsync` order, before ShowRunner starts):
  1. Check Ollama: `GET /api/tags` → if `StationSettings.LlmModel` not present, call `POST /api/pull` and stream progress to `ILoggerFactory` + a `ModelDownloadProgressHub` SignalR broadcast.
  2. Check music sidecar: `GET /health` → if not ready, wait with 10 s retry (model downloads happen inside the sidecar's startup).
  3. Check TTS sidecar: same.
  4. Only when all sidecars healthy: set `ReadyToStream = true`; ShowRunner only starts when this is true.
- Console page shows the startup sequence in real time.

### 6.3 GPU selection
- Settings: `GpuLayerCount` (int, -1=auto). On save: call Ollama `POST /api/pull` with `num_gpu` parameter, or more practically: update the `OLLAMA_NUM_GPU` env var and signal a graceful Ollama restart (document in README: requires Aspire restart for now; full hot-reload is Phase 3).
- Music sidecar: accept `device` field in `/generate` (`"cuda"`, `"cpu"`, `"auto"`). Source from `MUSIC_DEVICE` env var, settable from admin (writes to a config file + triggers sidecar config reload endpoint `POST /config`).

### 6.4 Icecast stream metadata (now-playing for Winamp)
- When a new track starts: call Icecast admin URL `GET http://icecast:8000/admin/metadata?mount=/radio.mp3&mode=updinfo&song={artist}+-+{title}` (HTTP Basic auth with admin credentials). This pushes ICY metadata into the stream; Winamp/VLC reads it as "Now Playing".
- Encapsulate in `IIcecastMetadataClient` with a single `UpdateNowPlayingAsync` method. Call from PlayoutService when a Track item starts.
- In `appsettings.json`: `Icecast:AdminUser`, `Icecast:AdminPassword`.

### 6.5 Console page `/console`
- Remove sidebar console component.
- Add `/console` page: a scrolling `<pre>` or `<div>` fed by a `LogHub` SignalR channel.
- Custom `ILoggerProvider` in Web/Orchestrator: captures log lines to a bounded `Channel<string>` (max 1 000 entries), broadcast via `LogHub.BroadcastLog(line)`.
- Level filter buttons (Debug/Info/Warn/Error). Auto-scroll to bottom toggle.

**Accept:**
- On Air = off → Winamp plays silence, no errors.
- First AppHost run with empty Ollama volume: console page shows model download progress; ShowRunner doesn't start until model ready.
- Track change in Winamp shows correct "Artist – Title" in title bar.
- Admin music gen stop → no new generation jobs start.

---

## M7 — Statistics & Schedule Polish

### 7.1 Statistics page `/stats`
All data from DB queries. Sections:

**Station Overview**
- Total tracks, total artists, total announcements, total play time (sum of track durations).
- Uptime (AppHost start time from a singleton).
- Current listeners (Icecast admin `GET /admin/listclients` XML parse — show count, city if available from user-agent).

**Tracks**
- Top 10 by plays (bar chart using a simple HTML/CSS bar, no JS charting lib needed).
- Top 10 by net votes.
- Longest tracks, shortest tracks.
- Tracks per genre/subgenre (pie or stacked bar — CSS only).
- Recently retired tracks.

**Artists**
- Total artists, average tracks per artist.
- Top 5 most-played artists.
- Artists with most downvotes (candidates for retirement).
- Tracks per artist sorted by total plays.

**Hosts**
- Total airtime per host (sum of announcement durations + associated tracks during their slot).
- Talk count per host.
- Average talk length per host.
- Host rotation frequency (how often each host went on air per day).

**Formats**
- Hours on air per format.
- Most-played genre.
- Schedule coverage % (slots with FormatId / total slots).

**General**
- Play log heatmap: 7 days × 24 hours grid, cell = track count (CSS colour intensity).

### 7.2 Configurable frequency
- Settings: `FrequencyMhz` (double, range 87.5–108.0). Displayed in header: "WhipRadio 98.5 FM".
- Cosmetic only in Phase 2; could drive Icecast description field.

### 7.3 Music per format
- `MusicProductionService` now takes the current/next `Format` into account when selecting genre+subgenre for generation.
- Track selection (`TrackSelector`) filters by `Format.Genre` + `Format.SubGenre` when a format is active.
- If no matching tracks exist: generate one on-demand (synchronous, with a max 10-min timeout; announce "we're making something special" if it exceeds 2 min wait).

**Accept:**
- Stats page loads without error with ≥ 10 tracks, ≥ 2 hosts, ≥ 1 format.
- Listener count shows correct number (0 if none connected).
- Frequency shows in header after settings save.

---

## M8 — Bug Fixes & Robustness

Address the technical errors and language/voice issues found in testing.

### 8.1 IO abort errors (issue #47)
- Root cause: `CancellationToken` passed into ffmpeg process management is the host `stoppingToken`; when ASP.NET tears down (e.g. hot-reload, R8), the token cancels mid-stream, leaving the `Channel` writer in a broken state.
- Fix: use a dedicated `CancellationTokenSource` per playback item. Link it to `stoppingToken` but give it a 5 s grace period (`CancelAfterDelay`). Wrap all channel reads/writes in `try/catch OperationCanceledException` and handle gracefully (log info, not error).
- Announcement production: ensure `IAnnouncementDataSource.GetSummaryAsync` has its own timeout CTS (default 30 s); propagate cancellation without crashing the cycle.
- Add retry logic: `AnnouncementProductionService` and `MusicProductionService` catch all exceptions, log as Warning (not Error for cancellations), and resume after their configured backoff.

### 8.2 Language mixing (issue #48)
- ScriptWriter system prompt: explicitly add `"IMPORTANT: respond ONLY in {Language}. Never mix languages."` where `Language` comes from `Moderator.Language` (BCP-47, e.g. `"de-DE"`).
- VoiceDirector prompt: same constraint.
- `SpeechMarkerNormalizer`: add a `Language` parameter; for German, include German filler words ("äh", "ähm", "hm", "also"); for English, English ones ("uh", "y'know", "so"). Pass `Moderator.Language` through the pipeline.
- Post-generation sanity: run a trivial language-hint check (does German text contain at least 2 of: "der|die|das|und|ist|ich|ein"?). If not, log warning and regenerate once. No hard block — logging is enough for Phase 2.

### 8.3 All hosts same voice (issue #49)
- Ensure `DbInitializer` seeds each moderator with a **different** `VoiceId`. Add an assertion in the initializer: if two active moderators share a VoiceId and both are the same gender, log an error and pick a different available voice for the second one.
- In host creation form: voice picker shows currently-used voices with a "(in use)" label so admins avoid duplicates.

### 8.4 Electronic title sameness (issue #50)
- Already addressed in M4 via `recentTitleWords` exclusion list. Add specifically to the banned list seed: `["ghost", "neon", "echo", "static", "fade", "shadow", "pulse", "void", "dark", "night"]` — these are injected into the title prompt's exclusion list always (as a base list; the dynamic recent-words list is additional).

### 8.5 Regression test suite additions (≥ 15 new tests)
- IO cancellation: `PlayoutService` handles `OperationCanceledException` without crashing.
- Language enforcer: `ScriptWriterPromptBuilder` includes language instruction.
- Title exclusion: `MusicTitleGenerator` (extracted pure class) excludes banned words.
- VoiceId dedup: `DbInitializer` voice assignment produces unique IDs for 3 moderators.
- `IIcecastMetadataClient` sends correct HTTP params (mock handler test).
- `ProgramDirectorService` JSON schema validation rejects bad LLM output gracefully.

**Accept:**
- 60 min stress run: no IO-abort errors in Aspire logs.
- All 3 seeded moderators have different voice IDs.
- 20 new tracks generated: none of the 10 banned words appears in any title.
- Language-mixing test: ScriptWriter prompt for `"de-DE"` moderator contains "ONLY in de-DE".

---

## Definition of Done — Phase 2

- [ ] `dotnet build` + `dotnet test` green (≥ 50 unit tests total incl. Phase 1)
- [ ] Footer player persists across all page navigations
- [ ] SignalR: elapsed time ticks every second, now-playing updates instantly
- [ ] Winamp shows "Artist – Title" in title bar via ICY metadata
- [ ] Gender-correct voices for all seeded hosts
- [ ] Host rotation triggers ≤ 90 min; handover announcements play
- [ ] 5+ different announcement types observed in a 30-min run
- [ ] Weekly schedule grid renders; Program Director fills all slots
- [ ] Format visible on live page; disable-format flow works
- [ ] Artist table populated; library filterable by SubGenre
- [ ] Track durations vary (not all 1:30)
- [ ] Music generation throttles when queue full
- [ ] Banned title words not dominant; no exact title repeats
- [ ] Piper-DE TTS produces German speech
- [ ] ElevenLabs toggle in settings (key validation)
- [ ] OpenAI toggle in settings (key validation)
- [ ] Admin page: on-air, music-gen, announce-gen start/stop work
- [ ] Console page shows live log output
- [ ] Stats page loads with real data
- [ ] IO-abort errors eliminated (60-min soak)
- [ ] No language mixing (German host speaks German)

---

## Phase 3 Preview (do NOT implement in Phase 2)

- Crossfading and ducking (music volume ducks under announcements)
- Top-of-the-hour precision (song end aligns to :00)
- Podcast format support
- Host-to-host conversation segments
- Listener greetings / requests via web app
- Advertising spot generation
- News & traffic data sources (interfaces already exist)
- Mobile-optimised PWA with offline cache
- Multi-station (different streams, same Aspire host)
PLANEOF
echo "Phase2.md created, lines: $(wc -l < /home/claude/Phase2.md)"