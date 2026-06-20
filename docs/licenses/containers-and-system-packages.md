# Containers and System Package Licenses

Last reviewed: 2026-06-20

Sources used:

- Dockerfiles under `src/WhipRadio.Web`, `src/WhipRadio.Orchestrator`, and `sidecars/`
- `src/WhipRadio.AppHost/AppHost.cs`
- `start-studios.ps1`

Container and apt package licenses depend on the exact image digest and package version. If Docker images are published or handed to another party, generate an SBOM from the final image and include the package notices from the image layers.

## Runtime and Base Images

| Image or source | Used by | License notes |
| --- | --- | --- |
| `mcr.microsoft.com/dotnet/sdk:10.0` | Web and Orchestrator build stages | .NET source packages are generally MIT, but the container image includes Microsoft and Linux distribution notices. Verify the image digest and Microsoft container terms for release. |
| `mcr.microsoft.com/dotnet/aspnet:10.0` | Web and Orchestrator runtime stages | Same .NET/container notice requirement as above. |
| `python:3.11-slim` | TTS sidecar | Python Software Foundation license plus Debian package notices from the image. |
| `python:3.11-slim-bookworm` | Analysis and MusicGen sidecars | Python Software Foundation license plus Debian Bookworm package notices from the image. |
| `nvidia/cuda:12.8.1-runtime-ubuntu22.04` | ACE-Step sidecar | NVIDIA CUDA container/runtime terms plus Ubuntu package notices. |
| `ghcr.io/astral-sh/uv:0.7` | ACE-Step sidecar build copy | `uv` is dual licensed `MIT OR Apache-2.0`; keep its notices if redistributing. |
| `ollama/ollama:latest` | Writer Room container | Ollama runtime is MIT; downloaded models have separate licenses. |
| `libretime/icecast:latest` | Aspire Icecast container | Treat Icecast itself as GPL-2.0 family and verify the LibreTime image notices before redistributing. |
| `whipradio-acestep:local` | Local recording studio | Built from `sidecars/acestep/Dockerfile`; see Python/model docs. |
| `whipradio-musicgen:local` | Optional local MusicGen studio | Built from `sidecars/musicgen/Dockerfile`; see Python/model docs. |
| `whipradio-tts:local` | Local voice booth | Built from `sidecars/tts/Dockerfile`; contains `piper-tts` GPL-3.0-or-later if installed. |
| `whipradio-analysis:local` | Local audio analysis | Built from `sidecars/analysis/Dockerfile`; see Python/system package docs. |

## Apt Packages Installed by Dockerfiles

| Package | Used by | License notes |
| --- | --- | --- |
| `ffmpeg` | Orchestrator, analysis, MusicGen, ACE-Step | FFmpeg can be LGPL/GPL depending build options and linked codecs. Inspect `/usr/share/doc/ffmpeg/copyright` in the final image. |
| `libsndfile1`, `libsndfile1-dev` | Analysis, MusicGen, TTS, ACE-Step | libsndfile is LGPL-2.1 family; include package notices when distributing images. |
| `espeak-ng` | TTS sidecar | GPL-3.0-or-later family; used as Kokoro/misaki G2P fallback. |
| `build-essential`, `pkg-config` | MusicGen, ACE-Step build/runtime image layers | Build tooling under Debian/Ubuntu package licenses; prefer multi-stage cleanup if image redistribution becomes important. |
| `libavformat-dev`, `libavcodec-dev`, `libavdevice-dev`, `libavutil-dev`, `libavfilter-dev`, `libswscale-dev`, `libswresample-dev` | MusicGen | FFmpeg development libraries; same FFmpeg LGPL/GPL caveat. |
| `software-properties-common`, `git`, `curl`, `libffi-dev`, `libssl-dev`, `python3.11*` | ACE-Step | Ubuntu package licenses; include image package notices if redistributing. |

## Release Checklist

- Pin image digests for release builds instead of relying on mutable `latest` tags.
- Generate an SBOM for each final image.
- Copy `/usr/share/doc/*/copyright` notices for redistributed Debian/Ubuntu packages.
- Keep model licenses separate from container and Python package licenses.

## Reference Links

- .NET container images: <https://mcr.microsoft.com/en-us/product/dotnet/aspnet/about>
- Python official images: <https://hub.docker.com/_/python>
- NVIDIA CUDA images: <https://hub.docker.com/r/nvidia/cuda>
- uv: <https://github.com/astral-sh/uv>
- Ollama: <https://github.com/ollama/ollama>
- Icecast: <https://icecast.org/>
- FFmpeg legal information: <https://ffmpeg.org/legal.html>
