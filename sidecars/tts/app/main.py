"""WhipRadio TTS sidecar — FastAPI wrapper around a pluggable TTS engine.

Contract (Plan.md §7.1):
  GET  /health      -> {"status": "ok", "engine": "kokoro"}
  GET  /voices      -> [{"id", "language", "gender"}, ...]
  POST /synthesize  -> audio/wav (44.1 kHz, 16-bit, mono), X-Duration-Seconds header
"""

from __future__ import annotations

import io
import logging
from pathlib import Path

import numpy as np
import soundfile as sf
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel

from .kokoro_engine import KokoroEngine
from .markers import BreathSegment, PauseSegment, TextSegment, parse_segments
from .piper_engine import PiperEngine

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

TARGET_SAMPLE_RATE = 44100
BREATH_WAV = Path(__file__).resolve().parent.parent / "assets" / "breath.wav"

app = FastAPI(title="WhipRadio TTS sidecar")
ENGINES = {
    "kokoro": KokoroEngine(),
    "piper": PiperEngine(),
}
DEFAULT_ENGINE = "kokoro"
_breath_cache: dict[int, np.ndarray] = {}


class SynthesizeRequest(BaseModel):
    text: str
    voice: str = "af_heart"
    language: str = "en"
    rate: float = 1.0
    engine: str = DEFAULT_ENGINE


def _resample(audio: np.ndarray, source_rate: int, target_rate: int) -> np.ndarray:
    if source_rate == target_rate or audio.size == 0:
        return audio
    target_length = int(round(audio.size * target_rate / source_rate))
    positions = np.linspace(0, audio.size - 1, target_length)
    return np.interp(positions, np.arange(audio.size), audio).astype(np.float32)


def _breath_sample(target_rate: int) -> np.ndarray:
    if target_rate not in _breath_cache:
        data, rate = sf.read(BREATH_WAV, dtype="float32")
        if data.ndim > 1:
            data = data.mean(axis=1)
        _breath_cache[target_rate] = _resample(data, rate, target_rate)
    return _breath_cache[target_rate]


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "engine": DEFAULT_ENGINE, "engines": list(ENGINES.keys())}


@app.get("/voices")
def voices() -> list[dict]:
    result: list[dict] = []
    for engine in ENGINES.values():
        for voice in engine.voices():
            voice.setdefault("engine", engine.name)
            result.append(voice)
    return result


@app.post("/synthesize")
def synthesize(request: SynthesizeRequest) -> Response:
    segments = parse_segments(request.text)
    if not segments:
        raise HTTPException(status_code=400, detail="No synthesizable content in 'text'.")

    engine = ENGINES.get(request.engine, ENGINES[DEFAULT_ENGINE])
    voice = engine.resolve_voice(request.voice, request.language)
    base_rate = max(0.5, min(2.0, request.rate))
    parts: list[np.ndarray] = []

    for segment in segments:
        if isinstance(segment, TextSegment):
            speed = max(0.5, min(2.0, base_rate * segment.rate_factor))
            parts.append(engine.synthesize_segment(segment.text, voice, speed))
        elif isinstance(segment, PauseSegment):
            samples = int(engine.sample_rate * segment.milliseconds / 1000)
            parts.append(np.zeros(samples, dtype=np.float32))
        elif isinstance(segment, BreathSegment):
            parts.append(_breath_sample(engine.sample_rate))

    audio = np.concatenate([p for p in parts if p.size]) if parts else np.zeros(0, dtype=np.float32)
    if audio.size == 0:
        raise HTTPException(status_code=400, detail="Synthesis produced no audio.")

    audio = _resample(audio, engine.sample_rate, TARGET_SAMPLE_RATE)
    duration = audio.size / TARGET_SAMPLE_RATE

    buffer = io.BytesIO()
    sf.write(buffer, audio, TARGET_SAMPLE_RATE, format="WAV", subtype="PCM_16")
    return Response(
        content=buffer.getvalue(),
        media_type="audio/wav",
        headers={"X-Duration-Seconds": f"{duration:.3f}"},
    )
