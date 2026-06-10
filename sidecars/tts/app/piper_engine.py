"""Piper TTS engine — the local engine with German voices (thorsten, eva_k, …).

Voice models download on first use from rhasspy/piper-voices into HF_HOME.
"""

from __future__ import annotations

import logging

import numpy as np

from .engine_base import EngineBase

logger = logging.getLogger(__name__)

# voice id -> (hf path, language, gender)
KNOWN_VOICES = {
    "de_DE-thorsten-medium": ("de/de_DE/thorsten/medium/de_DE-thorsten-medium", "de", "m"),
    "de_DE-eva_k-x_low": ("de/de_DE/eva_k/x_low/de_DE-eva_k-x_low", "de", "f"),
    "de_DE-karlsson-low": ("de/de_DE/karlsson/low/de_DE-karlsson-low", "de", "m"),
    "en_US-ryan-medium": ("en/en_US/ryan/medium/en_US-ryan-medium", "en", "m"),
    "en_US-lessac-medium": ("en/en_US/lessac/medium/en_US-lessac-medium", "en", "f"),
}

DEFAULT_DE_VOICE = "de_DE-thorsten-medium"
DEFAULT_EN_VOICE = "en_US-lessac-medium"


class PiperEngine(EngineBase):
    name = "piper"
    sample_rate = 22050  # most piper voices; per-voice rate handled in synthesize

    def __init__(self) -> None:
        self._voices: dict[str, object] = {}

    def _load(self, voice_id: str):
        if voice_id not in self._voices:
            from huggingface_hub import hf_hub_download
            from piper import PiperVoice

            path, _, _ = KNOWN_VOICES[voice_id]
            logger.info("Loading Piper voice %s (first time downloads the model)", voice_id)
            onnx = hf_hub_download("rhasspy/piper-voices", f"{path}.onnx")
            config = hf_hub_download("rhasspy/piper-voices", f"{path}.onnx.json")
            self._voices[voice_id] = PiperVoice.load(onnx, config_path=config)
        return self._voices[voice_id]

    def resolve_voice(self, voice: str, language: str) -> str:
        if voice in KNOWN_VOICES:
            return voice
        fallback = DEFAULT_DE_VOICE if language.startswith("de") else DEFAULT_EN_VOICE
        logger.warning("Piper voice '%s' unknown; falling back to %s", voice, fallback)
        return fallback

    def synthesize_segment(self, text: str, voice: str, speed: float) -> np.ndarray:
        piper_voice = self._load(voice)
        rate = getattr(piper_voice.config, "sample_rate", 22050)

        chunks: list[np.ndarray] = []
        length_scale = 1.0 / max(0.5, min(2.0, speed))  # piper: bigger = slower
        try:
            stream = piper_voice.synthesize_stream_raw(text, length_scale=length_scale)
            for chunk in stream:
                chunks.append(np.frombuffer(chunk, dtype=np.int16).astype(np.float32) / 32768.0)
        except AttributeError:
            # Newer piper API: synthesize() yields AudioChunk objects.
            for chunk in piper_voice.synthesize(text):
                data = getattr(chunk, "audio_int16_bytes", None) or getattr(chunk, "audio", b"")
                if isinstance(data, bytes):
                    chunks.append(np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0)
                rate = getattr(chunk, "sample_rate", rate)

        if not chunks:
            return np.zeros(0, dtype=np.float32)

        audio = np.concatenate(chunks)
        # Engines report a fixed sample_rate; resample if this voice differs.
        if rate != self.sample_rate:
            target_length = int(round(audio.size * self.sample_rate / rate))
            positions = np.linspace(0, audio.size - 1, target_length)
            audio = np.interp(positions, np.arange(audio.size), audio).astype(np.float32)
        return audio

    def voices(self) -> list[dict]:
        return [
            {"id": vid, "language": lang, "gender": gender, "engine": self.name}
            for vid, (_, lang, gender) in KNOWN_VOICES.items()
        ]
