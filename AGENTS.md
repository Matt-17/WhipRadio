# Repository Guidelines

## Project Structure & Module Organization

WhipRadio is a .NET 10 Aspire solution. The solution file is `WhipRadio.slnx` (XML solution format — there is no `.sln`). Core domain logic lives in `src/WhipRadio.Core` (no infrastructure dependencies), infrastructure integrations and EF Core persistence in `src/WhipRadio.Infrastructure`, long-running radio services, API endpoints, and the SignalR hub in `src/WhipRadio.Orchestrator`, and the Blazor Server console in `src/WhipRadio.Web`. `src/WhipRadio.AppHost` wires Aspire resources together (PostgreSQL, Icecast, Orchestrator, Web); `src/WhipRadio.ServiceDefaults` holds shared telemetry/health/resilience defaults. Tests are under `tests/` (`WhipRadio.Core.Tests`, `WhipRadio.Infrastructure.Tests`, `WhipRadio.Orchestrator.Tests`, shared helpers in `tests/TestSupport`). Python/FastAPI sidecars live in `sidecars/` (`tts`, `musicgen`, `analysis`, `acestep`). `tools/` contains `WhipRadio.DbMigrator` and DB backup scripts. Deployment support is in `deploy/`; phase planning documents are at the repository root.

Before larger changes, read `ARCHITECTURE.md` (runtime map, main flows, concurrency/resource ownership) and, for UI work, `DESIGN-GUIDE.md`.

## Build, Test, and Development Commands

- `.\build.ps1`: builds the full solution (`dotnet build WhipRadio.slnx`).
- `.\test.ps1`: builds the solution and runs **all** .NET tests (`dotnet test WhipRadio.slnx`).
- `.\start.ps1` / `.\stop.ps1` / `.\restart.ps1`: build and launch or stop the local AppHost workflow (logs to `apphost-run.log`).
- `.\start-studios.ps1`, `.\stop-studios.ps1`, `.\restart-studios.ps1`: manage long-lived Writer Room, music, voice, and analysis containers outside the AppHost lifecycle.
- `.\test-studios.ps1`: probes local studio endpoints and verifies the Writer Room model (default `gemma4:e4b`) can generate.
- `.\build-sidecars.ps1`: builds all sidecar images tagged `whipradio-*:local` (GPU/CPU torch wheels auto-detected).
- `pytest sidecars/analysis/tests -q`: runs analysis sidecar tests after installing its Python requirements.

**Never start, stop, or restart the app yourself** — the user manages the station via `start.ps1`/`stop.ps1`. Finish work by building and testing; do not run the program. The station is often running in the background while you work: if a build fails on locked output files, redirect with an **absolute** `-p:OutDir=<path outside the repo>` (a relative `OutDir` pollutes the repository with nested output trees).

## Database & Entity Framework

The relational store is **PostgreSQL** (Aspire `AddPostgres` in `src/WhipRadio.AppHost/AppHost.cs`); SQLite is gone — `tools/postgres_sqlite_backup.ps1` is only a migration/backup helper. Infrastructure and Orchestrator tests run against real Postgres via `Testcontainers.PostgreSql` using `tests/TestSupport` (`DbFixture`, `PostgresTestDatabase`).

When changing `RadioDbContext` entities or indexes, scaffold real EF Core migrations instead of hand-writing migration/designer/snapshot files: `dotnet ef migrations add <Name> --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator`, then inspect the generated migration for destructive operations. Verify with `dotnet ef migrations list ... --no-build` and `dotnet ef migrations has-pending-model-changes ... --no-build`. If the station is running and locking the Orchestrator output, use `--startup-project src/WhipRadio.Infrastructure` (it has the design-time `RadioDbContextFactory`); do not trust stale `--no-build` output from a locked Orchestrator build. Runtime schema application belongs in startup via `DbInitializer.EnsureSeededAsync`; API endpoints and read paths must not run pending migrations. Add `DbInitializer` patch logic only for safe data defaults or recovery of partially migrated local databases.

## LLM Calls & Structured JSON

All LLM outputs are schema-constrained JSON — never parse free-form model text. To add a new structured output: define a typed record DTO (mark required properties like `TextDto` does), pass `StructuredJson.SchemaFor<T>()` as `TextGenerationRequest.ResponseSchema`, and parse the reply with `StructuredJson.Parse<T>()` (returns `StructuredJsonResult<T>`). The machinery lives in `src/WhipRadio.Core/Json/StructuredJson.cs`; canonical examples are `src/WhipRadio.Infrastructure/Llm/MusicCopywriter.cs` and `AnnouncementWriter.cs`. Provider selection is settings-driven via routers (`TextGenerationRouter` for Ollama/OpenAI, `TtsEngineRouter`, `StudioProviderFactory`) — code against `ITextGenerationService`/`ITtsEngine`, not concrete providers. Prompt text assets live under `src/WhipRadio.Infrastructure/Prompts/`.

## HTTP Clients & Resilience

`ServiceDefaults` enables `AddStandardResilienceHandler()` for all HTTP clients by default. The AI and long-running clients (Ollama, OpenAI, ElevenLabs, studios, analysis) **deliberately strip it** in `src/WhipRadio.Infrastructure/HttpClientsServiceCollectionExtensions.cs` via `RemoveAllResilienceHandlers()` (plus `HardenForLongRunningCalls()` where needed), because the standard ~10s attempt timeout and retries cancel and duplicate full model generations. Never re-add retry/timeout handlers to these clients to "fix" a slow AI call; register new AI clients through the same extension file following the existing pattern.

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces, nullable reference types and implicit usings enabled, `LangVersion` `latest`. Project, XML, and JSON files use 2-space indentation. PascalCase for types, methods, properties, and public fields; camelCase for locals and parameters; `_camelCase` for private fields; `s_camelCase` for private static fields. Prefer explicit types over `var` unless the type is obvious from object creation. For fire-and-forget tasks, use the `Forget()` extension from `src/WhipRadio.Core/Helpers/TaskExtensions.cs` — never discard a task with `_ =` or leave it unobserved.

## Web UI & SignalR

Before changing Blazor pages, shared components, or `src/WhipRadio.Web/wwwroot/app.css`, read `DESIGN-GUIDE.md`. Preserve the late-night broadcast-console design language. Reuse the shared components in `src/WhipRadio.Web/Components/` instead of building page-local variants: `Modal`, `ConfirmDialog`, `StatusBadge`, `UiSwitch`, `Icon`, `TimeAgo`, `ProgressBar`, `PersonAvatar`, and friends. Destructive actions use a trash icon that opens `ConfirmDialog` — never inline confirmations. Treat switches as immediate-save controls for binary active/off state, placed as the final item in their row or action cluster. Use `StatusBadge` for operator states (Active, Pending, Queued, REC, Recording, Failed, Off); do not create page-local state tag spans. Ask the user before choosing unresolved UI/UX directions such as dense tables versus cards or major layout changes.

Real-time updates flow through the single SignalR `RadioHub` at `/hubs/radio` (`src/WhipRadio.Orchestrator/Api/RadioHub.cs`); the Web side consumes it through per-feature `*LiveClient` services in `src/WhipRadio.Web/Services/`. Add new events to the existing hub and a matching live client — do not add new hubs or ad-hoc polling. In the chat UI, failed or rejected agent actions go to the agent logs, never into the chat stream; the chat must read like a consumer messenger.

## Language & Voices

All admin-console UI text is **English**, terse radio-console voice. The broadcast/written on-air language is a **station setting** — never hardcode on-air strings to English or German. `moderator.Language` describes voice accent only, not content language. All host voices are designed `qv-` Qwen voices (see `HostVoicePreparationService.DesignedVoicePrefix`); voices are timbre-only and language-agnostic — never assign Kokoro/Piper engine presets to hosts and never couple a voice to a language; pass the target language per synthesis call.

## Host Creation & Station Context

Host hiring must stay easy: collect a short optional hint and let the program director/backend decide name, gender, persona, traits, talk profile, and voice description. Do not add frontend name lists, gender pickers, engine selectors, model selectors, or voice-id fields unless the user explicitly asks for an expert mode. Specialist host creation follows the artist-creation pattern: close the modal immediately, show queued/creating/failed status on the page, and use structured JSON returned by the writer room instead of frontend-built persona strings.

Station description is mandatory context for host creation and on-air specialist prompts: include station name, slogan, vision, mission, audience/format context, and any manual hint. News and weather selectors must not offer "automatic" or "first available" wording; the user either chooses a host or leaves it for the program director to create one when needed. If no suitable news/weather specialist exists at runtime, create one rather than skipping the segment.

## Testing Guidelines

.NET tests use MSTest with `coverlet.collector` available. Name test classes after the subject (`WeightedTrackSelectorTests`) with descriptive method names stating the expected behavior. Keep Core tests free of infrastructure dependencies; put DB-backed tests in Infrastructure/Orchestrator test projects using the `TestSupport` fixtures. Sidecar tests use synthetic fixtures rather than committed binary media. `tests/ManualTests/` holds manual notes, not automated tests.

## Commit & Pull Request Guidelines

Recent history uses concise conventional-style prefixes such as `fix:`, `feat:`, and `perf:`. Keep commits focused and imperative. Pull requests should include a short description, test commands run, linked issues when applicable, and screenshots or audio/API notes for UI, streaming, or generation behavior changes.

## Security & Configuration Tips

Do not commit secrets, local model caches, generated data roots, or production credentials. Icecast passwords and API keys live in `.env` (gitignored; copy from `.env.example`) or real environment variables — there are no baked-in dev defaults in committed code and none may be added. The Aspire AppHost loads `.env` at startup on every platform and seeds the environment for all resources; real env vars always override `.env`.

## Third-Party Licenses

Maintain `docs/licenses/` whenever adding, removing, or upgrading third-party dependencies: NuGet `PackageReference` entries, Python packages, Docker base images, apt packages, AI model ids, model weight repositories, voice repositories, and external APIs. Record the version or constraint, source, license/SPDX identifier when known, and any special restriction (non-commercial terms, copyleft duties, model terms, cloud service terms, voice consent). For AI dependencies, document the wrapper package license and the model/service terms separately.

## Architecture Decision Notes

When changing model defaults, studio ownership, images, voices, or audio behavior, update `Phase-0-Tech-Decisions.md` and, if work remains, `Phase-0-Deferred.md`.
