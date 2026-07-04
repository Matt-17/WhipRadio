# WhipRadio Architecture

WhipRadio is a local-first AI radio station built as a .NET 10 Aspire solution.
The system is split into three runtime layers:

1. The Aspire app graph: PostgreSQL, Icecast, the Orchestrator, and the Blazor web console.
2. The studio layer: long-lived AI sidecars started by repo-root scripts.
3. The data root: generated audio, cached state, and model-adjacent assets (durable
   station state lives in PostgreSQL).

The main architectural rule is that the Orchestrator owns radio behavior. The
web app presents and controls state, the studio services provide external
capabilities, and Core/Infrastructure provide reusable domain and adapter code.

## Runtime Map

```text
AppHost (src/WhipRadio.AppHost/AppHost.cs)
|-- postgres      persistent PostgreSQL container (radio db), named data volume
|-- icecast       persistent libretime/icecast container, :8000/radio.mp3
|-- orchestrator  long-running station workers, API, SignalR, ffmpeg playout
`-- web           Blazor Server console and same-origin media proxy

Studio services (start-studios.ps1)
|-- writer room   Ollama + gemma4:e4b, http://localhost:8001 (container-internal 11434)
|-- studio        ACE-Step 1.5 music generation, http://localhost:8101
|-- booth         local TTS service, http://localhost:8201
`-- analysis      audio analysis service, http://localhost:8301

Data root (Radio:DataRoot, usually ./data in development)
|-- library/tracks/*.wav
|-- announcements, jingles, generated media
`-- voice references and other generated support files
(durable station state lives in the PostgreSQL container, not the data root)
```

Aspire deliberately does not own Ollama, ACE-Step, TTS, or analysis. Those
services survive app restarts and are operated through `start-studios.ps1`,
`stop-studios.ps1`, `restart-studios.ps1`, and `test-studios.ps1`.

## Projects

### `src/WhipRadio.Core`

Core contains domain contracts, entities, audio helpers, playout abstractions,
prompt context models, and logic that should not depend on EF Core, HTTP,
Docker, Blazor, or sidecar implementation details.

Use Core when behavior needs to be shared by the Orchestrator, Infrastructure,
and tests. Keep it free of runtime adapters.

### `src/WhipRadio.Infrastructure`

Infrastructure implements external adapters and persistence:

- EF Core persistence through `RadioDbContext`.
- text generation clients and routing.
- music generation providers, including `musicgen` and `ace-step-1.5`.
- TTS clients and voice design clients.
- weather, analysis, and studio endpoint integration.

The infrastructure layer should hide provider-specific transport details behind
Core abstractions such as `IMusicGenerator`, `IMusicGenerationProvider`,
`ITextGenerationService`, and `ITtsEngine`.

### `src/WhipRadio.Orchestrator`

The Orchestrator is the station brain. It exposes HTTP APIs and SignalR hubs,
owns all long-running radio workers, and is the only process that should mutate
station runtime state.

Important hosted services include:

- `PlayoutRecoveryService`: restores persisted playout state on startup.
- `PlayoutService`: feeds raw PCM into the long-lived ffmpeg encoder.
- `ShowRunnerService`: builds the live radio sequence.
- `MusicProductionService`: creates artist tracks.
- `AnnouncementProductionService`: creates spoken segments.
- `ProgramDirectorService`: plans and adjusts scheduled formats.
- `MessageModerationService`: handles listener messages.
- `NightlyModeratorMemoryDistillationService`: compresses host memory.
- `TalkBreakCleanupService`: removes stale generated talk content.
- `AnalysisBackfillService`: backfills audio analysis.
- `ConsoleLogBroadcaster`: publishes live logs to the console UI.
- `ChatAgentWorker`: runs queued host/director chat turns through the writer room.
- `ChatCleanupService`: trims retained chat history and expires stale pending actions.

Startup responsibilities also live here:

- resolve `Radio:DataRoot` and the PostgreSQL connection string (`ConnectionStrings:radio`);
- run `DbInitializer.EnsureSeededAsync`;
- kill stale ffmpeg processes from previous runs;
- align moderator language with the station language;
- map radio APIs and `/hubs/radio`.

### `src/WhipRadio.Web`

The web project is a Blazor Server operator and listener console. It should stay
thin: it calls Orchestrator APIs through `RadioApiClient`, subscribes to SignalR
through live clients, and renders state.

The web app also owns same-origin media proxy routes:

- `/media/live`
- `/media/track/{id}`
- `/media/announcement/{id}`
- `/media/jingle/{id}`
- `/media/voice-preview/{handle}`

These routes avoid browser mixed-content problems and hide internal service
names from the browser.

### `src/WhipRadio.AppHost`

AppHost wires the local Aspire app graph:

- creates the persistent Icecast container;
- passes `Radio__DataRoot`, Icecast settings, and `Llm__Endpoint` to the
  Orchestrator;
- gives the web app a reference to the Orchestrator;
- sets `Stream__PublicUrl` for browser playback.

AppHost does not start or restart studio sidecars.

### `src/WhipRadio.ServiceDefaults`

ServiceDefaults contains shared Aspire defaults for service discovery,
health checks, telemetry, and resilience used by the .NET projects.

### `sidecars`

Sidecars are Python/FastAPI or containerized AI services. They are operational
dependencies, not domain owners.

- `sidecars/acestep`: ACE-Step 1.5 music generation API wrapper.
- `sidecars/musicgen`: MusicGen provider.
- `sidecars/tts`: local voice/TTS service.
- `sidecars/analysis`: audio analysis API.

Keep MusicGen and ACE-Step separate. `musicgen` and `ace-step-1.5` are distinct
provider IDs; `ace-step` is only an accepted alias that normalizes to
`ace-step-1.5`.

## Main Flows

### Startup

1. `start-studios.ps1` starts or reuses the studio containers.
2. `start.ps1` or `dotnet run --project src/WhipRadio.AppHost` starts Aspire.
3. AppHost starts Icecast, then the Orchestrator, then the web app.
4. The Orchestrator initializes the database, state, ffmpeg registry, hosted
   services, and SignalR endpoints.

### Playout

1. `ShowRunnerService` chooses tracks, talk, jingles, and special segments.
   Approaching a scheduled news/weather package it consults the pure
   `TimingPlanner`: cap the track pick to the remaining gap, bridge small gaps
   with a station-ID jingle, or stop enqueueing and let the dispatcher land the
   package (the mixer's timed-interrupt fade is the last resort; music is never
   time-stretched).
2. Items are queued through `IPlayoutQueue`.
3. `PlayoutService` decodes each item to raw PCM through short-lived ffmpeg
   readers.
4. `AudioMixerEngine` and the long-lived ffmpeg encoder write MP3 to Icecast.
5. `PlaybackReporter` updates now-playing state, play logs, web clients, and
   Icecast metadata with listener-facing latency compensation.

### Music Generation

1. `MusicProductionService` chooses or creates an artist.
2. `MusicCopywriter` plans the song title, style, language, lyrics, target
   duration, and story.
3. `IMusicGenerator` routes to the requested provider, the station default, or
   fallback behavior.
4. `MusicGenGenerationProvider` handles MusicGen requests.
5. `AceStepGenerationProvider` handles ACE-Step requests through the async
   `/release_task`, `/query_result`, and `/v1/audio` API flow.
6. Generated WAV files are stored under the data root and indexed in PostgreSQL.

ACE-Step should generate one complete song. Do not add MusicGen-style
continuation, chunking, or stitching to ACE-Step paths.

### Voice Continuity

For vocal ACE-Step songs, artist history can provide:

- artist/member voice prompts;
- a short reference-audio clip (the designed lead-vocalist voice) uploaded as `ref_audio`.

The reference audio sent to ACE-Step should be short. Sending a whole previous
song makes ACE-Step spend minutes decoding reference audio before generation.

### Talk And Announcements

1. `ShowRunnerService`, `ProgramDirectorService`, or listener/message flows
   request spoken content.
2. prompt context is built by `PromptContextBuilder`.
3. text comes from the Writer Room through the text generation router.
4. delivery is adapted by the voice director.
5. TTS renders WAV audio through the configured booth.
6. announcements are queued for playout and stored for diagnostics.

### Chat Control

1. The Blazor Chat page calls `/api/chat` and subscribes to `/hubs/radio`.
2. `ChatService` owns channel bootstrap, persisted messages, admin read state,
   action result JSON, and SignalR events (`ChatMessageAdded`,
   `ChatChannelUpdated`, `ChatAgentThinking`).
3. Admin messages are resolved by `ChatResponderResolver` and queued through
   `ChatTurnQueue`; `ChatAgentWorker` creates a scoped `ChatAgentTurnService`
   per turn.
4. `PromptContextBuilder` builds `PromptScope.Chat` context with persona,
   station state, recent chat history, and role-available `ICharacterTool`s.
5. `ChatReplyParser` reads the JSON envelope, validates actions against the
   catalog, and the bounded retry path asks the agent to correct malformed
   output.
6. `ChatActionExecutor` dispatches actions to existing station services:
   `AnnouncementFactory`, `PriorityTalkBreakDispatcher`,
   `DirectorPlanningService`, `SpecialistHostCreationService`,
   `TrackQueryService`, and `ChatService`.
7. Fast lookup actions such as `SearchMusic` and `StatusReport` run inside the
   agent turn and feed their results back to the model before the final reply.
8. Host-to-host `Message` actions create normalized one-to-one channels,
   preserve correlation/hop counts, enforce the hop cap, and create a scheduled
   `TalkBreak` when a terminal Admin report ends the exchange.
9. `ChatNotificationBus` lets existing services publish proactive System
   messages for director results, production failures, and show handovers.

## State And Persistence

PostgreSQL is the source of truth for durable station state. Runtime APIs and read
paths must not run pending migrations. Schema changes belong in EF migrations,
and startup applies migration/seed/default recovery through
`DbInitializer.EnsureSeededAsync`.

Use the EF workflow from `AGENTS.md` when changing entities or indexes:

```powershell
dotnet ef migrations add <Name> --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator
dotnet ef migrations list --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build
dotnet ef migrations has-pending-model-changes --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build
```

When the running station locks the Orchestrator output, use the Infrastructure
startup project or an isolated artifacts path rather than trusting stale build
output.

## Concurrency And Resource Ownership

Studio usage goes through `StudioCoordinator` and related routers. The goal is
that expensive GPU work is serialized within the same GPU group, especially
music generation, TTS, and local LLM work.

Manual user requests may bypass normal production pacing, but they should still
respect studio availability, queueing, and GPU ownership.

## Boundaries For Future Changes

- Put reusable business rules in Core.
- Put provider-specific HTTP, EF, Docker, and filesystem adapter code in
  Infrastructure.
- Put long-running station workflows in the Orchestrator.
- Keep the Web project focused on UI, API calls, SignalR state, and media proxy
  routes.
- Keep studio sidecars operationally separate from Aspire.
- Do not fold ACE-Step into MusicGen or replace MusicGen with ACE-Step.
- Do not make API handlers responsible for migrations or background workflow
  orchestration.
- Prefer raw diagnostics and exact runtime state in operator-facing screens.

## Verification

Common validation commands:

```powershell
dotnet build WhipRadio.slnx
dotnet test WhipRadio.slnx
.\test.ps1
.\test-studios.ps1
```

If the running station locks normal build outputs, use an isolated artifacts
path:

```powershell
dotnet test WhipRadio.slnx --artifacts-path D:\tmp\whipradio-artifacts
```
