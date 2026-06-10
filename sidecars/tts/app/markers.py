"""Speech-marker parsing — mirrors the C# SpeechMarkerNormalizer contract.

Markers: [pause:NNNms] (clamped 100-1500), [breath], [rate:slow|normal|fast].
Unknown bracket tags are stripped; consecutive breath markers collapse to one.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

TAG_RE = re.compile(r"\[([^\[\]]*)\]")
PAUSE_RE = re.compile(r"^pause\s*:?\s*(\d+)\s*(ms)?$")
RATE_FACTORS = {"slow": 0.8, "normal": 1.0, "fast": 1.2}
MIN_PAUSE_MS = 100
MAX_PAUSE_MS = 1500


@dataclass
class TextSegment:
    text: str
    rate_factor: float


@dataclass
class PauseSegment:
    milliseconds: int


@dataclass
class BreathSegment:
    pass


Segment = TextSegment | PauseSegment | BreathSegment


def parse_segments(text: str) -> list[Segment]:
    """Split marked-up text into ordered text/pause/breath segments."""
    segments: list[Segment] = []
    rate_factor = 1.0
    pos = 0

    def push_text(chunk: str) -> None:
        chunk = chunk.strip()
        if chunk:
            segments.append(TextSegment(chunk, rate_factor))

    for match in TAG_RE.finditer(text):
        push_text(text[pos : match.start()])
        pos = match.end()

        tag = match.group(1).strip().lower()
        if tag == "breath":
            if not (segments and isinstance(segments[-1], BreathSegment)):
                segments.append(BreathSegment())
            continue

        pause = PAUSE_RE.match(tag)
        if pause:
            ms = max(MIN_PAUSE_MS, min(MAX_PAUSE_MS, int(pause.group(1))))
            segments.append(PauseSegment(ms))
            continue

        if tag.startswith("rate:"):
            rate = tag[len("rate:") :].strip()
            if rate in RATE_FACTORS:
                rate_factor = RATE_FACTORS[rate]
            continue

        # Unknown tag: stripped.

    push_text(text[pos:])
    return segments
