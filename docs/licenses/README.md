# Third-Party License Inventory

Last reviewed: 2026-06-20

This folder tracks the third-party license surface WhipRadio currently depends on. It is a practical engineering inventory, not legal advice. Before distributing WhipRadio, a hosted service, generated media, or Docker images, verify the upstream terms again and keep the resulting notices with the release artifacts.

## Scope

- [dotnet-nuget.md](dotnet-nuget.md): .NET SDK, direct NuGet packages, and resolved transitive NuGet package licenses.
- [python-sidecars.md](python-sidecars.md): Python sidecar requirements and Python packages installed in Dockerfiles.
- [containers-and-system-packages.md](containers-and-system-packages.md): Docker base images, runtime containers, and OS packages installed by sidecars.
- [models-and-services.md](models-and-services.md): AI model weights, local model runtimes, and external APIs.
- [fonts-and-web-assets.md](fonts-and-web-assets.md): locally served web fonts and browser-facing web asset privacy notes.

## High-attention items

- `facebook/musicgen-small` model weights are `CC-BY-NC-4.0`. Treat MusicGen output as non-commercial unless the upstream model terms change or a separate license is obtained.
- `piper-tts` is listed on PyPI as `GPL-3.0-or-later`. The `rhasspy/piper-voices` model repository is MIT, but distributing a container with the Python package can trigger GPL obligations.
- Gemma licensing depends on the exact model generation. The repo default is `gemma4:e4b`; Gemma 4 is documented separately from older Gemma terms. Verify the exact Ollama tag before release.
- FFmpeg licensing depends on how the binary was built. Debian/Ubuntu packages can include LGPL and GPL components. Treat every distributed image containing FFmpeg as needing package-level notice review.
- Container images contain OS packages beyond the application dependencies. If images are redistributed, generate an image SBOM and include the package notices from the image layers.

## Maintenance Rule

Update this folder whenever a change adds, removes, or upgrades any of the following:

- a `PackageReference`, .NET SDK, or test package;
- a Python package in `requirements.txt`, a Dockerfile, or an upstream sidecar lock file;
- a Docker base image, runtime image, or apt package;
- a browser-facing web font, CDN asset, external stylesheet, or external script;
- an AI model id, model weight repository, voice repository, or LoRA/training dependency;
- an external API or hosted service used by the station.

For AI systems, record the wrapper code license and the model/service terms separately. A permissive Python package does not automatically make the downloaded model weights or hosted API output permissive.
