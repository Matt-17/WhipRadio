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

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces are preferred, nullable reference types and implicit usings are enabled, and `LangVersion` is `latest`. Project, XML, and JSON files use 2-space indentation. Use PascalCase for types, methods, properties, and public fields; camelCase for locals and parameters; `_camelCase` for private fields; `s_camelCase` for private static fields. Prefer explicit types over `var` unless the type is obvious from object creation.

## Testing Guidelines

.NET tests use MSTest with `coverlet.collector` available. Name test classes after the subject, for example `WeightedTrackSelectorTests`, and use descriptive test method names that state the expected behavior. Keep Core tests free of infrastructure dependencies. Sidecar tests should use synthetic fixtures rather than committed binary media.

## Entity Framework & Schema Changes

When changing `RadioDbContext` entities or indexes, scaffold real EF Core migrations instead of hand-writing migration/designer/snapshot files. Use `dotnet ef migrations add <Name> --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator`, then inspect the generated migration for destructive operations. Verify migration discovery and snapshot alignment with `dotnet ef migrations list --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build` and `dotnet ef migrations has-pending-model-changes --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build`. If the station is running and locking/staling the Orchestrator output, either stop it first or use `--startup-project src/WhipRadio.Infrastructure`, which has the design-time `RadioDbContextFactory`; do not trust stale `--no-build` output from a locked Orchestrator process. Runtime schema application belongs in startup via `DbInitializer.EnsureSeededAsync`; API endpoints and read paths must not run pending migrations. Add `DbInitializer` patch logic only for safe data defaults or recovery of partially migrated local databases.

## Commit & Pull Request Guidelines

Recent history uses concise conventional-style prefixes such as `fix:`, `feat:`, and `perf:`. Keep commits focused and imperative. Pull requests should include a short description, test commands run, linked issues when applicable, and screenshots or audio/API notes for UI, streaming, or generation behavior changes.

## Security & Configuration Tips

Do not commit secrets, local model caches, generated data roots, or production credentials. Development defaults such as `Icecast__SourcePassword=hackme-dev` are for local use only. Prefer user secrets or environment variables for API keys and machine-specific paths.

## Architecture Decision Notes

When changing model defaults, studio ownership, images, voices, or audio behavior, update `Phase-0-Tech-Decisions.md` and, if work remains, `Phase-0-Deferred.md`.
