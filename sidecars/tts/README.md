# WhipRadio TTS sidecar

FastAPI service wrapping the **Kokoro** TTS engine (swappable via `EngineBase`).

## API (port 8001)

| Endpoint | Description |
|---|---|
| `GET /health` | `{"status":"ok","engine":"kokoro"}` |
| `GET /voices` | available voices `[{"id","language","gender"}]` |
| `POST /synthesize` | body `{"text","voice","language","rate"}` → `audio/wav` (44.1 kHz, 16-bit, mono) + `X-Duration-Seconds` header |

Speech markers in `text`: `[pause:NNNms]` (100–1500), `[breath]`, `[rate:slow|normal|fast]`.

## Local smoke test

```bash
pip install -r requirements.txt
uvicorn app.main:app --port 8001
```

```bash
curl -X POST http://localhost:8001/synthesize \
  -H "Content-Type: application/json" \
  -d '{"text":"Hello [pause:300ms] world [breath] again","voice":"af_heart","language":"en","rate":1.0}' \
  -o out.wav
ffprobe out.wav   # expect: pcm_s16le, 44100 Hz, mono, duration >= speech + 0.3 s pause + breath
```

First synthesize call downloads the Kokoro model into `HF_HOME` (mounted at `/models` in the container) — allow a few minutes.

## Docker

```bash
docker build -t whip-radio-tts sidecars/tts
docker run --rm -p 8001:8001 -v hf-cache:/models whip-radio-tts
```

## Assets

`assets/breath.wav` is generated (filtered noise burst, 250 ms) — regenerate with
`python scripts/make_breath.py`. No third-party audio involved.
