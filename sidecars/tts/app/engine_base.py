"""Engine abstraction so additional TTS backends (e.g. XTTS) can be added later."""

from __future__ import annotations

from abc import ABC, abstractmethod

import numpy as np


class EngineBase(ABC):
    name: str
    sample_rate: int

    @abstractmethod
    def synthesize_segment(self, text: str, voice: str, speed: float) -> np.ndarray:
        """Synthesize one plain-text segment to float32 mono audio at self.sample_rate."""

    @abstractmethod
    def voices(self) -> list[dict]:
        """[{"id": ..., "language": ..., "gender": ...}, ...]"""

    def resolve_voice(self, voice: str, language: str) -> str:
        """Map a requested voice/language to one this engine supports."""
        return voice

    def unload(self) -> dict:
        """Release cached model state. Engines without resident state can no-op."""
        return {"engine": self.name, "unloaded": False}

    def status(self) -> dict:
        """Small runtime description for /health and studio UI labels."""
        return {
            "engine": self.name,
            "sample_rate_hz": self.sample_rate,
        }
