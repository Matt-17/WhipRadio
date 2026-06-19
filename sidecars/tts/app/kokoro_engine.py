"""Kokoro TTS engine (https://github.com/hexgrad/kokoro).

Kokoro is English-centric; unsupported languages fall back to an English voice
with a logged warning (Plan.md risk table) so the ITtsEngine contract holds.
"""

from __future__ import annotations

import gc
import logging

import numpy as np

from .engine_base import EngineBase

logger = logging.getLogger(__name__)

# Voice ids are prefixed with their Kokoro lang code: a = American, b = British English.
KNOWN_VOICES = [
    {"id": "af_heart", "language": "en", "gender": "f"},
    {"id": "af_bella", "language": "en", "gender": "f"},
    {"id": "af_nicole", "language": "en", "gender": "f"},
    {"id": "am_michael", "language": "en", "gender": "m"},
    {"id": "am_adam", "language": "en", "gender": "m"},
    {"id": "bf_emma", "language": "en", "gender": "f"},
    {"id": "bm_george", "language": "en", "gender": "m"},
]

DEFAULT_VOICE = "af_heart"


class KokoroEngine(EngineBase):
    name = "kokoro"
    sample_rate = 24000

    def __init__(self) -> None:
        self._pipelines: dict[str, object] = {}

    def _pipeline(self, lang_code: str):
        # Lazy import + lazy model download: keeps /health responsive on first start.
        if lang_code not in self._pipelines:
            from kokoro import KPipeline

            logger.info("Loading Kokoro pipeline for lang_code=%s", lang_code)
            self._pipelines[lang_code] = KPipeline(lang_code=lang_code)
        return self._pipelines[lang_code]

    def resolve_voice(self, voice: str, language: str) -> str:
        if any(v["id"] == voice for v in KNOWN_VOICES):
            return voice
        logger.warning(
            "Voice '%s' (language '%s') not available in Kokoro; falling back to %s",
            voice,
            language,
            DEFAULT_VOICE,
        )
        return DEFAULT_VOICE

    def synthesize_segment(self, text: str, voice: str, speed: float) -> np.ndarray:
        pipeline = self._pipeline(voice[0])
        chunks: list[np.ndarray] = []
        for result in pipeline(text, voice=voice, speed=speed):
            audio = result[-1] if isinstance(result, tuple) else result.audio
            if audio is None:
                continue
            if hasattr(audio, "detach"):  # torch tensor
                audio = audio.detach().cpu().numpy()
            chunks.append(np.asarray(audio, dtype=np.float32))
        if not chunks:
            return np.zeros(0, dtype=np.float32)
        return np.concatenate(chunks)

    def voices(self) -> list[dict]:
        return KNOWN_VOICES

    def status(self) -> dict:
        return {
            "engine": self.name,
            "label": "Kokoro TTS",
            "sample_rate_hz": self.sample_rate,
            "voices": len(KNOWN_VOICES),
            "resident_loaded": bool(self._pipelines),
        }

    def unload(self) -> dict:
        loaded = bool(self._pipelines)
        self._pipelines.clear()
        gc.collect()
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception:  # noqa: BLE001 - torch may not be imported/available
            logger.debug("Kokoro unload skipped torch cache cleanup", exc_info=True)

        logger.info("Unloaded Kokoro pipelines: %s", loaded)
        return {"engine": self.name, "unloaded": loaded}
