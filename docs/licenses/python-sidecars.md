# Python Sidecar Licenses

Last reviewed: 2026-06-20

Sources used:

- `sidecars/*/requirements.txt`
- `sidecars/*/Dockerfile`
- PyPI package metadata for direct packages
- Upstream model pages for model weights, which are tracked separately in [models-and-services.md](models-and-services.md)

The Python sidecars do not currently use lock files. Version ranges can resolve to different dependency versions over time. Before shipping a Docker image, run a package license scan inside the built image and attach the resulting third-party notices.

## Analysis Sidecar

Declared in `sidecars/analysis/requirements.txt`.

| Package | Version constraint | License metadata |
| --- | --- | --- |
| `fastapi` | `==0.115.6` | MIT |
| `uvicorn[standard]` | `==0.32.1` | BSD-3-Clause |
| `librosa` | `==0.10.2.post1` | ISC |
| `pyloudnorm` | `==0.1.1` | MIT |
| `soundfile` | `==0.12.1` | BSD-3-Clause |
| `numpy` | `<2` | BSD-3-Clause plus bundled notices (`0BSD`, MIT, Zlib, CC0-1.0 in current PyPI metadata) |

The Dockerfile also installs `libsndfile1` and `ffmpeg`; those are tracked in [containers-and-system-packages.md](containers-and-system-packages.md).

## MusicGen Sidecar

Declared in `sidecars/musicgen/requirements.txt` and `sidecars/musicgen/Dockerfile`.

| Package | Version constraint | License metadata |
| --- | --- | --- |
| `torch` | `==2.1.0` from PyTorch wheel index | BSD-style / BSD-3-Clause family; keep wheel notices |
| `torchaudio` | `==2.1.0` from PyTorch wheel index | PyTorch project license family; keep wheel notices |
| `audiocraft` | `>=1.3` | MIT |
| `transformers` | `>=4.31,<4.40` | Apache-2.0 |
| `huggingface_hub` | `<1.0` | Apache-2.0 |
| `fastapi` | `>=0.115` | MIT |
| `uvicorn[standard]` | `>=0.30` | BSD-3-Clause |
| `soundfile` | `>=0.12` | BSD-3-Clause |
| `numpy` | `<2` | BSD-3-Clause plus bundled notices |

The `facebook/musicgen-small` model license is not the same as the `audiocraft` code license. It is tracked in [models-and-services.md](models-and-services.md).

## TTS Sidecar

Declared in `sidecars/tts/requirements.txt` and `sidecars/tts/Dockerfile`.

| Package | Version constraint | License metadata |
| --- | --- | --- |
| `torch` | `==2.8.0` by Docker build arg | BSD-style / BSD-3-Clause family; keep wheel notices |
| `torchaudio` | `==2.8.0` by Docker build arg | PyTorch project license family; keep wheel notices |
| `transformers` | `>=4.40,<5` | Apache-2.0 |
| `fastapi` | `>=0.115` | MIT |
| `uvicorn[standard]` | `>=0.30` | BSD-3-Clause |
| `kokoro` | `>=0.9` | Apache-2.0 |
| `piper-tts` | `>=1.2` | GPL-3.0-or-later |
| `qwen-tts` | unpinned | Apache-2.0 |
| `huggingface_hub` | unpinned | Apache-2.0 |
| `soundfile` | `>=0.12` | BSD-3-Clause |
| `numpy` | `>=1.26` | BSD-3-Clause plus bundled notices |

`piper-tts` is the strongest copyleft item in the Python sidecar list. If WhipRadio distributes the TTS Docker image, review GPL source and notice obligations for that image.

## ACE-Step Sidecar

Declared in `sidecars/acestep/Dockerfile`.

| Component | Version/ref | License metadata |
| --- | --- | --- |
| `ACE-Step-1.5` | git ref `dce621408bee8c31b4fcf4811682eb9359e1bc94` | MIT |
| `torch` | `==2.8.0` from CUDA 12.8 wheel index | BSD-style / BSD-3-Clause family; keep wheel notices |
| `torchvision` | `==0.23.0` from CUDA 12.8 wheel index | BSD family; keep wheel notices |
| `torchaudio` | `==2.8.0` from CUDA 12.8 wheel index | PyTorch project license family; keep wheel notices |
| `uv` | copied from `ghcr.io/astral-sh/uv:0.7` | MIT OR Apache-2.0 |

The ACE-Step Dockerfile runs `uv sync --frozen --no-dev` against the upstream ACE-Step lock file at the pinned git ref. Its full Python dependency graph is therefore upstream-controlled and should be scanned from the built image for release.

## Reference Links

- PyPI: <https://pypi.org/>
- FastAPI: <https://pypi.org/project/fastapi/>
- Uvicorn: <https://pypi.org/project/uvicorn/>
- librosa: <https://pypi.org/project/librosa/>
- pyloudnorm: <https://pypi.org/project/pyloudnorm/>
- SoundFile: <https://pypi.org/project/soundfile/>
- NumPy: <https://pypi.org/project/numpy/>
- AudioCraft: <https://pypi.org/project/audiocraft/>
- Transformers: <https://pypi.org/project/transformers/>
- Hugging Face Hub: <https://pypi.org/project/huggingface-hub/>
- Kokoro: <https://pypi.org/project/kokoro/>
- Piper TTS: <https://pypi.org/project/piper-tts/>
- Qwen TTS: <https://pypi.org/project/qwen-tts/>
- PyTorch: <https://pypi.org/project/torch/>
- TorchAudio: <https://pypi.org/project/torchaudio/>
- TorchVision: <https://pypi.org/project/torchvision/>

## Recommended Image Scan

For release builds, run a license scan inside each built sidecar image, for example with `pip-licenses` or an SBOM tool. Keep the direct inventory above as the human-maintained review list, but use image scans for exact transitive Python package notices.
