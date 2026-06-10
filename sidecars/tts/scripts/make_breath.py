"""Generates assets/breath.wav: a 250 ms low-passed noise burst with fade in/out.

Pure stdlib so it runs anywhere: python scripts/make_breath.py
"""

import math
import random
import struct
import wave
from pathlib import Path

SAMPLE_RATE = 44100
DURATION_MS = 250
AMPLITUDE = 0.18

random.seed(1701)  # reproducible asset

n_samples = SAMPLE_RATE * DURATION_MS // 1000
noise = [random.uniform(-1.0, 1.0) for _ in range(n_samples)]

# Cheap low-pass: moving average over ~0.5 ms windows makes it sound breathy.
window = 24
smoothed = []
acc = 0.0
for i, sample in enumerate(noise):
    acc += sample
    if i >= window:
        acc -= noise[i - window]
    smoothed.append(acc / min(i + 1, window))

# Sine-shaped envelope: fade in and out over the whole burst.
samples = []
for i, sample in enumerate(smoothed):
    envelope = math.sin(math.pi * i / n_samples)
    samples.append(int(max(-1.0, min(1.0, sample * envelope * AMPLITUDE * 4)) * 32767))

out_path = Path(__file__).resolve().parent.parent / "assets" / "breath.wav"
out_path.parent.mkdir(parents=True, exist_ok=True)
with wave.open(str(out_path), "wb") as wav:
    wav.setnchannels(1)
    wav.setsampwidth(2)
    wav.setframerate(SAMPLE_RATE)
    wav.writeframes(struct.pack(f"<{len(samples)}h", *samples))

print(f"Wrote {out_path} ({len(samples)} samples, {DURATION_MS} ms)")
