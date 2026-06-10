"""Instrumental music via Meta's MusicGen (audiocraft).

Model: MUSICGEN_MODEL env, default facebook/musicgen-small. CPU works (slowly).
Tracks longer than MusicGen's 30 s window are produced by windowed continuation.
"""

from __future__ import annotations

import logging
import os

import numpy as np

logger = logging.getLogger(__name__)

WINDOW_SECONDS = 30
OVERLAP_SECONDS = 10


class MusicGenBackend:
    name = "musicgen"

    def __init__(self) -> None:
        self._model = None

    def available(self) -> bool:
        return True

    def _get_model(self):
        if self._model is None:
            from audiocraft.models import MusicGen

            model_name = os.environ.get("MUSICGEN_MODEL", "facebook/musicgen-small")
            logger.info("Loading MusicGen model %s (first time downloads weights)", model_name)
            self._model = MusicGen.get_pretrained(model_name)
        return self._model

    def generate(self, prompt: str, duration_seconds: int, lyrics: str | None = None) -> tuple[np.ndarray, int]:
        """Returns (float32 mono samples, sample_rate)."""
        import torch

        model = self._get_model()
        sample_rate = model.sample_rate

        first_window = min(WINDOW_SECONDS, duration_seconds)
        model.set_generation_params(duration=first_window)
        with torch.no_grad():
            wav = model.generate([prompt])[0]  # (channels, samples)

        while wav.shape[-1] < duration_seconds * sample_rate:
            context = wav[..., -OVERLAP_SECONDS * sample_rate :]
            remaining = duration_seconds - wav.shape[-1] / sample_rate
            model.set_generation_params(duration=min(WINDOW_SECONDS, OVERLAP_SECONDS + remaining))
            with torch.no_grad():
                continued = model.generate_continuation(
                    context.unsqueeze(0), prompt_sample_rate=sample_rate, descriptions=[prompt]
                )[0]
            new_audio = continued[..., context.shape[-1] :]
            if new_audio.shape[-1] == 0:
                logger.warning("Continuation produced no new audio; stopping at %.1f s", wav.shape[-1] / sample_rate)
                break
            wav = torch.cat([wav, new_audio], dim=-1)

        wav = wav[..., : duration_seconds * sample_rate]
        mono = wav.mean(dim=0).clamp(-1.0, 1.0).cpu().numpy().astype(np.float32)
        return mono, sample_rate
