# WhipRadio ACE-Step sidecar

Dedicated ACE-Step 1.5 container for complete local song generation.

- Upstream repository: `https://github.com/ace-step/ACE-Step-1.5`
- Pinned ref: `dce621408bee8c31b4fcf4811682eb9359e1bc94`
- API approach: official ACE-Step async REST API (`/release_task`,
  `/query_result`, `/v1/audio`)
- Exposes port `8002`
- Stores model weights and runtime caches under `/models`

The Docker build installs ACE-Step with the upstream `uv.lock`. Model weights
are not downloaded during image build; they are downloaded on first generation
into the mounted `/models` volume.

## Build

```bash
docker build -t whipradio-acestep sidecars/acestep
```

## CPU

```bash
docker run --rm \
  -p 8002:8002 \
  -v acestep-models:/models \
  whipradio-acestep
```

## NVIDIA GPU

```bash
docker run --rm \
  --gpus all \
  -p 8002:8002 \
  -v acestep-models:/models \
  whipradio-acestep
```
