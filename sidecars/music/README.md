# WhipRadio music sidecar

FastAPI service generating the station's record collection.

- **musicgen** (default): instrumental tracks via Meta MusicGen (`audiocraft`),
  model `facebook/musicgen-small` (override with `MUSICGEN_MODEL`). CPU works — slowly.
  Tracks > 30 s are produced via windowed continuation.
- **ace-step**: vocal tracks; heavyweight and **opt-in** via `ENABLE_ACESTEP=1`
  (requires installing the `acestep` package). Disabled ⇒ `/generate` answers 503
  and `/health` reports `"ace-step": false`; the orchestrator then produces
  instrumental tracks only.

## API (port 8002)

| Endpoint | Description |
|---|---|
| `GET /health` | `{"status":"ok","backends":{"musicgen":true,"ace-step":false}}` |
| `POST /generate` | body `{"prompt","backend","duration_seconds","lyrics"}` → `audio/wav` + `X-Backend` header |

Generation is synchronous and long-running — use a generous client timeout (30 min).

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
ffprobe track.wav   # expect duration ≈ 30 s (±10%)
```

A 90 s generation on CPU can take several minutes — that is expected; the first
call additionally downloads the model weights into `HF_HOME` (`/models` volume).

## Docker

```bash
docker build -t whip-radio-music sidecars/music
docker run --rm -p 8002:8002 -v hf-cache:/models whip-radio-music
```
