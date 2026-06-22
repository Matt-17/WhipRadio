# Repository Guidelines

## Project Structure & Module Organization

WhipRadio is a .NET 10 Aspire solution. Core domain logic lives in `src/WhipRadio.Core`, infrastructure integrations in `src/WhipRadio.Infrastructure`, long-running radio services and API endpoints in `src/WhipRadio.Orchestrator`, and the Blazor Server console in `src/WhipRadio.Web`. `src/WhipRadio.AppHost` wires Aspire resources together. Unit tests are under `tests/WhipRadio.Core.Tests` and `tests/WhipRadio.Infrastructure.Tests`. Python/FastAPI sidecars live in `sidecars/` for TTS, music generation, audio analysis, and ACE-Step. Deployment support is in `deploy/`, and phase planning documents are kept at the repository root.

## Build, Test, and Development Commands

- `dotnet build WhipRadio.slnx`: builds the full solution.
- `dotnet test WhipRadio.slnx`: runs all .NET tests.
- `.\test.ps1`: runs the Core and Infrastructure test projects only; safe while the station is running.
- `dotnet run --project src/WhipRadio.AppHost`: starts the Aspire AppHost locally.
- `.\start.ps1` / `.\stop.ps1`: build and launch or stop the local AppHost workflow.
- `.\start-studios.ps1`, `.\stop-studios.ps1`, `.\restart-studios.ps1`: manage long-lived Writer Room, recording, voice, and analysis containers.
- `.\test-studios.ps1`: probes local studio endpoints and verifies Gemma 4 can generate.
- `pytest sidecars/analysis/tests -q`: runs analysis sidecar tests after installing its Python requirements.
- `docker build -t whipradio-acestep sidecars/acestep`: builds an individual sidecar image when needed.
- Always build and test the project as last step. Running the Program is not recommended, as it's running in the background.

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces are preferred, nullable reference types and implicit usings are enabled, and `LangVersion` is `latest`. Project, XML, and JSON files use 2-space indentation. Use PascalCase for types, methods, properties, and public fields; camelCase for locals and parameters; `_camelCase` for private fields; `s_camelCase` for private static fields. Prefer explicit types over `var` unless the type is obvious from object creation.

## Web UI Design Guide

Before changing Blazor pages, shared components, or `src/WhipRadio.Web/wwwroot/app.css`, read `DESIGN-GUIDE.md`. Preserve the existing late-night broadcast-console design language. Treat switches as immediate-save controls for binary active/off state, always place them as the final item in their row or action cluster. Use the shared `StatusBadge` component for changing operator states such as Active, Pending, Queued, REC, Recording, Failed, and Off; do not create page-local state tag spans. Ask the user before choosing unresolved UI/UX directions such as dense tables versus cards, major layout changes, or icon-only versus text actions.

## Host Creation & Station Context

Host hiring must stay easy: collect a short optional hint and let the program director/backend decide name, gender, persona, traits, talk profile, and voice description. Do not add frontend name lists, gender pickers, engine selectors, model selectors, or voice-id fields unless the user explicitly asks for an expert mode. Specialist host creation should follow the artist-creation pattern: close the modal immediately, show queued/creating/failed status on the page, and use structured JSON returned by the writer room instead of frontend-built persona strings.

Station description is mandatory context for host creation and on-air specialist prompts. Include station name, slogan, vision, mission, audience/format context, and any manual hint when creating news or weather specialists. News and weather selectors should not offer "automatic" or "first available" wording; the user either chooses a host or leaves it for the program director to create one when needed. If no suitable news/weather specialist exists at runtime, create one rather than skipping the segment by default.

## Testing Guidelines

.NET tests use MSTest with `coverlet.collector` available. Name test classes after the subject, for example `WeightedTrackSelectorTests`, and use descriptive test method names that state the expected behavior. Keep Core tests free of infrastructure dependencies. Sidecar tests should use synthetic fixtures rather than committed binary media.

## Entity Framework & Schema Changes

When changing `RadioDbContext` entities or indexes, scaffold real EF Core migrations instead of hand-writing migration/designer/snapshot files. Use `dotnet ef migrations add <Name> --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator`, then inspect the generated migration for destructive operations. Verify migration discovery and snapshot alignment with `dotnet ef migrations list --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build` and `dotnet ef migrations has-pending-model-changes --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build`. If the station is running and locking/staling the Orchestrator output, either stop it first or use `--startup-project src/WhipRadio.Infrastructure`, which has the design-time `RadioDbContextFactory`; do not trust stale `--no-build` output from a locked Orchestrator process. Runtime schema application belongs in startup via `DbInitializer.EnsureSeededAsync`; API endpoints and read paths must not run pending migrations. Add `DbInitializer` patch logic only for safe data defaults or recovery of partially migrated local databases.

## Commit & Pull Request Guidelines

Recent history uses concise conventional-style prefixes such as `fix:`, `feat:`, and `perf:`. Keep commits focused and imperative. Pull requests should include a short description, test commands run, linked issues when applicable, and screenshots or audio/API notes for UI, streaming, or generation behavior changes.

## Security & Configuration Tips

Do not commit secrets, local model caches, generated data roots, or production credentials. Icecast passwords and API keys live in `.env` (gitignored; copy from `.env.example`) or real environment variables — there are no baked-in dev defaults in committed code. The Aspire AppHost loads `.env` at startup on every platform (Windows/Linux/macOS/WSL) and seeds the environment for all resources; real env vars always override `.env`.

## Third-Party Licenses

Maintain `docs/licenses/` whenever adding, removing, or upgrading third-party dependencies. This includes NuGet `PackageReference` entries, Python packages, Docker base images, apt packages, AI model ids, model weight repositories, voice repositories, and external APIs. Record the version or constraint, source, license/SPDX identifier when known, and any special restriction such as non-commercial terms, copyleft distribution duties, model terms, cloud service terms, or voice consent requirements. For AI dependencies, document the wrapper package license and the model/service terms separately.

## Architecture Decision Notes

When changing model defaults, studio ownership, images, voices, or audio behavior, update `Phase-0-Tech-Decisions.md` and, if work remains, `Phase-0-Deferred.md`.
