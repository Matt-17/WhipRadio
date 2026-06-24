# WhipRadio ACE-Step sidecar

Dedicated ACE-Step 1.5 container for complete local song generation.

- Upstream repository: `https://github.com/ace-step/ACE-Step-1.5`
- Pinned ref: `dce621408bee8c31b4fcf4811682eb9359e1bc94`
- API approach: official ACE-Step async REST API (`/release_task`,
  `/query_result`, `/v1/audio`)
- Exposes port `8002`
- Stores model weights and runtime caches under `/models`
- Reads curated WhipRadio `ref_audio` voice references from `/app/data`

The Docker build installs ACE-Step with the upstream `uv.lock`, then pins the
PyTorch runtime to the CUDA 12.8 wheel stack (`torch==2.8.0`,
`torchvision==0.23.0`, `torchaudio==2.8.0`). Model weights are not downloaded
during image build; they are downloaded on first generation into the mounted
`/models` volume.

WhipRadio's `start-studios.ps1` mounts the station data directory read-only at
`/app/data` for ACE-Step. This lets vocal song generation upload the designed
lead-vocalist voice clip directly as a `ref_audio` reference.

`start-studios.ps1` also sets:

- `ACESTEP_GENERATION_TIMEOUT=600` — hard cap for a single song. A generation
  that exceeds it is aborted (job → failed, a terminal state), so a slow song
  never looks like a wedge.
- `ACESTEP_STUCK_TIMEOUT_SECONDS=1200` — wedge detector. If jobs are pending but
  none reach a terminal state for this long, the watchdog restarts the API
  server. Must stay greater than `ACESTEP_GENERATION_TIMEOUT`.

## Build

```bash
docker build -t whipradio-acestep sidecars/acestep
```

## CPU

```bash
docker run --rm \
  -p 8002:8002 \
  -v acestep-models:/models \
  -v /path/to/WhipRadio/data:/app/data:ro \
  whipradio-acestep
```

## NVIDIA GPU

```bash
docker run --rm \
  --gpus all \
  -p 8002:8002 \
  -v acestep-models:/models \
  -v /path/to/WhipRadio/data:/app/data:ro \
  whipradio-acestep
```
