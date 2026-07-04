# Sidecars

Python/FastAPI studio services, built as local Docker images by
`build-sidecars.ps1` (tagged `whipradio-*:local`) and run by
`start-studios.ps1` outside the AppHost lifecycle. The writer room is not a
sidecar in this folder — it is the stock Ollama image managed by the same
script.

## Port topology

The internal (container) ports are historical and differ per image; the
**host** ports follow the `8x01` layout and are what the Orchestrator, the
seeded studio rows, and `ServiceEndpointDefaults` use.

| Service | Container | Image | Internal port | Host port (default) |
|---|---|---|---|---|
| Writer room (Ollama) | `whip-writer-room-ollama` | `ollama/ollama` | 11434 | **8001** |
| Recording studio (ACE-Step) | `whip-studio-acestep-N` | `whipradio-acestep:local` | 8002 | **8100 + N** (Studio #1 → 8101) |
| Recording studio (MusicGen, optional) | `whip-studio-musicgen-1` | `whipradio-musicgen:local` | 8002 | **8111** |
| Voice booth (TTS) | `whip-booth-tts-1` | `whipradio-tts:local` | 8001 | **8201** |
| Analysis | `whip-analysis` | `whipradio-analysis:local` | 8301 | **8301** |

Related host ports outside this folder: Icecast `8000` (public stream),
Orchestrator `5151`, PostgreSQL (Aspire-managed).

The single source of truth for the *mapping* is `start-studios.ps1`
(`Ensure-Container <name> <image> <hostPort> <containerPort> …`); the single
source of truth for the *defaults consumed by .NET code* is
`src/WhipRadio.Core/Configuration/ServiceEndpointDefaults.cs`. Change both
together.

## Dependency pinning

- `analysis/requirements.txt` — exact `==` pins (CPU-only, cheap to rebuild).
- `tts/requirements.txt` — exact `==` pins taken from the verified working
  image's `pip freeze`; **torch is deliberately unpinned** because the wheel
  variant (CPU/CUDA) is selected at build time via the `TORCH_INDEX` build arg.
- `musicgen` — ranged pins on purpose: the MusicGen studio is optional
  (`-IncludeMusicGen`) and no verified local image exists to derive exact
  versions from. Pin from a working image's `pip freeze` before relying on it.
- `acestep` — no requirements file of its own; the image builds from the
  upstream ACE-Step repository via `uv sync --frozen` (upstream lockfile).

The FastAPI boilerplate (health payload shape, WAV response writing, the
per-process generation lock) is intentionally duplicated per sidecar: each
image builds from its own directory as the Docker context, so a shared module
would need build-script copy steps for ~50 lines of code. Revisit only if the
sidecars grow a real shared surface.

When adding or upgrading anything here, update `docs/licenses/` per the
repository guidelines.
