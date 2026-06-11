# WhipRadio Analysis Sidecar

CPU-only audio analysis for the mixer (Phase 3a): BPM + beat grid, intro/outro
detection, silence trim points, EBU R128 loudness, true peak, and a 2 Hz energy
profile. Source-agnostic — operates on WAV files below `DATA_ROOT`, so generated
and imported tracks flow through the identical path.

## API

```
GET  /health
POST /analyze   { "path": "library/tracks/{id}.wav", "mode": "music" | "speech" }
```

`mode=speech` (announcements) skips beats/intro/outro and returns loudness,
silence, duration and energy only.

## Algorithms

- **BPM/beats:** `librosa.beat.beat_track` on the percussive component
  (`librosa.effects.hpss`). Confidence = normalised tempogram peak ratio.
  Octave guard: results < 70 BPM are tested against 2× BPM on the tempogram.
- **Intro end:** first time the 2 Hz RMS curve stays ≥ 55 % of the track median
  for ≥ 2 s, cross-checked against the cumulative `onset_strength` jump (15 %).
  Confidence from the agreement of the two detectors.
- **Outro start:** start of the final region where RMS stays < 45 % of median
  for ≥ 3 s through the end of the track.
- **Silence:** `librosa.effects.trim` boundaries at `top_db=40`.
- **Loudness:** `pyloudnorm.Meter(...).integrated_loudness`; true peak from a
  4× oversampled absolute maximum.

A 7-minute WAV must analyse in < 20 s on CPU (asserted in the tests).

## Run

```bash
docker build -t whipradio-analysis:local sidecars/analysis
docker run -d --name whip-analysis -p 8301:8301 -v /path/to/data:/data:ro whipradio-analysis:local
```

`start-studios.ps1` does this automatically with the repo's `data` folder.

## Tests

```bash
pip install -r requirements.txt pytest httpx
pytest sidecars/analysis/tests -q
```
