"""WhipRadio music sidecar - FastAPI wrapper around MusicGen.

Contract:
  GET  /health    -> {"status":"ok","backends":{"musicgen":true}}
  POST /generate  -> audio/wav (long-running; clients use generous timeouts)
"""

from __future__ import annotations

import io
import logging
import threading

import soundfile as sf
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel

from .backends.musicgen_backend import MusicGenBackend

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="WhipRadio music sidecar")

BACKENDS = {
    "musicgen": MusicGenBackend(),
}

# One generation at a time: model inference saturates the machine anyway.
_generation_lock = threading.Lock()


class GenerateRequest(BaseModel):
    prompt: str
    backend: str = "musicgen"
    duration_seconds: int = 90
    lyrics: str | None = None


@app.get("/health")
def health() -> dict:
    return {
        "status": "ok",
        "backends": {name: backend.available() for name, backend in BACKENDS.items()},
    }


@app.post("/generate")
def generate(request: GenerateRequest) -> Response:
    backend = BACKENDS.get(request.backend)
    if backend is None:
        raise HTTPException(status_code=400, detail=f"Unknown backend '{request.backend}'.")
    if not backend.available():
        raise HTTPException(status_code=503, detail=f"Backend '{request.backend}' is unavailable.")

    duration = max(10, min(600, request.duration_seconds))
    logger.info("Generating %s s with %s: %s", duration, request.backend, request.prompt)

    with _generation_lock:
        samples, sample_rate = backend.generate(request.prompt, duration, request.lyrics)

    buffer = io.BytesIO()
    sf.write(buffer, samples, sample_rate, format="WAV", subtype="PCM_16")
    logger.info("Generated %.1f s of audio", len(samples) / sample_rate)

    return Response(
        content=buffer.getvalue(),
        media_type="audio/wav",
        headers={"X-Backend": request.backend},
    )
