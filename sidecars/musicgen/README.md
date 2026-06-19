# WhipRadio music sidecar

FastAPI service generating instrumental tracks with Meta MusicGen.

- **musicgen**: instrumental tracks via Meta MusicGen (`audiocraft`), model
  `facebook/musicgen-small` by default. Override with `MUSICGEN_MODEL`.
- CPU works, slowly.
- Tracks longer than MusicGen's 30 second window are produced via the existing
  windowed continuation logic.

ACE-Step is intentionally not installed in this image. It runs as the separate
`sidecars/acestep` service.

## API (port 8002)

| Endpoint | Description |
|---|---|
| `GET /health` | `{"status":"ok","service":"whipradio-musicgen","label":"...","backends":{"musicgen":true}}` |
| `POST /generate` | body `{"prompt","backend","duration_seconds","lyrics"}` -> `audio/wav` + `X-Backend` header |

Generation is synchronous and long-running. Use a generous client timeout.

## Local smoke test

```bash
pip install -r requirements.txt
uvicorn app.main:app --port 8002
```

```bash
curl -X POST http://localhost:8002/generate \
  -H "Content-Type: application/json" \
  -d '{"prompt":"energetic indie rock, driving drums","backend":"musicgen","duration_seconds":30}' \
  -o track.wav
ffprobe track.wav
```

## Docker

```bash
docker build -t whipradio-musicgen sidecars/musicgen
docker run --rm -p 8002:8002 -v hf-cache:/models whipradio-musicgen
```
