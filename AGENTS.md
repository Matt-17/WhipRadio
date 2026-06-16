# Repository Guidelines

## Project Structure & Module Organization

WhipRadio is a .NET 10 Aspire solution. Core domain logic lives in `src/WhipRadio.Core`, infrastructure integrations in `src/WhipRadio.Infrastructure`, long-running radio services and API endpoints in `src/WhipRadio.Orchestrator`, and the Blazor Server console in `src/WhipRadio.Web`. `src/WhipRadio.AppHost` wires Aspire resources together. Unit tests are under `tests/WhipRadio.Core.Tests` and `tests/WhipRadio.Infrastructure.Tests`. Python/FastAPI sidecars live in `sidecars/` for TTS, music generation, audio analysis, and ACE-Step. Deployment support is in `deploy/`, and phase planning documents are kept at the repository root.

## Build, Test, and Development Commands

- `dotnet build WhipRadio.slnx`: builds the full solution.
- `dotnet test WhipRadio.slnx`: runs all .NET tests.
- `.\test.ps1`: runs the Core and Infrastructure test projects only; safe while the station is running.
- `dotnet run --project src/WhipRadio.AppHost`: starts the Aspire AppHost locally.
- `.\start.ps1` / `.\stop.ps1`: build and launch or stop the local AppHost workflow.
- `pytest sidecars/analysis/tests -q`: runs analysis sidecar tests after installing its Python requirements.
- `docker build -t whipradio-acestep sidecars/acestep`: builds an individual sidecar image when needed.

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces are preferred, nullable reference types and implicit usings are enabled, and `LangVersion` is `latest`. Project, XML, and JSON files use 2-space indentation. Use PascalCase for types, methods, properties, and public fields; camelCase for locals and parameters; `_camelCase` for private fields; `s_camelCase` for private static fields. Prefer explicit types over `var` unless the type is obvious from object creation.

## Testing Guidelines

.NET tests use xUnit with `coverlet.collector` available. Name test classes after the subject, for example `WeightedTrackSelectorTests`, and use descriptive test method names that state the expected behavior. Keep Core tests free of infrastructure dependencies. Sidecar tests should use synthetic fixtures rather than committed binary media.

## Commit & Pull Request Guidelines

Recent history uses concise conventional-style prefixes such as `fix:`, `feat:`, and `perf:`. Keep commits focused and imperative. Pull requests should include a short description, test commands run, linked issues when applicable, and screenshots or audio/API notes for UI, streaming, or generation behavior changes.

## Security & Configuration Tips

Do not commit secrets, local model caches, generated data roots, or production credentials. Development defaults such as `Icecast__SourcePassword=hackme-dev` are for local use only. Prefer user secrets or environment variables for API keys and machine-specific paths.
