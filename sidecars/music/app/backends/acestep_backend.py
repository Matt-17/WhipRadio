"""Vocal music via ACE-Step — heavyweight, therefore opt-in (ENABLE_ACESTEP=1).

When disabled or not importable, available() is False and the API answers 503;
the orchestrator then falls back to instrumental MusicGen tracks (Plan.md risks).
"""

from __future__ import annotations

import logging
import os
import tempfile

import numpy as np

logger = logging.getLogger(__name__)


class AceStepBackend:
    name = "ace-step"

    def __init__(self) -> None:
        self._pipeline = None
        self._enabled = os.environ.get("ENABLE_ACESTEP", "0") == "1"
        self._importable: bool | None = None

    def available(self) -> bool:
        if not self._enabled:
            return False
        if self._importable is None:
            try:
                import acestep  # noqa: F401

                self._importable = True
            except ImportError:
                logger.warning("ENABLE_ACESTEP=1 but the 'acestep' package is not installed.")
                self._importable = False
        return self._importable

    def generate(self, prompt: str, duration_seconds: int, lyrics: str | None = None) -> tuple[np.ndarray, int]:
        if not self.available():
            raise RuntimeError("ACE-Step backend is not available.")

        import soundfile as sf
        from acestep.pipeline_ace_step import ACEStepPipeline

        if self._pipeline is None:
            logger.info("Loading ACE-Step pipeline (first time downloads weights)")
            self._pipeline = ACEStepPipeline(dtype="float32")

        with tempfile.TemporaryDirectory() as tmp:
            output_path = os.path.join(tmp, "out.wav")
            self._pipeline(
                prompt=prompt,
                lyrics=lyrics or "",
                audio_duration=float(duration_seconds),
                save_path=output_path,
            )
            data, sample_rate = sf.read(output_path, dtype="float32")

        if data.ndim > 1:
            data = data.mean(axis=1)
        return data.astype(np.float32), sample_rate
