"""WhipRadio audio analysis sidecar.

POST /analyze computes BPM + beat grid, intro/outro boundaries, silence,
EBU R128 loudness and a 2 Hz energy profile for a WAV below DATA_ROOT.
Source-agnostic: generated and imported media take the identical path.
"""

import os

import librosa
import numpy as np
import pyloudnorm
import soundfile as sf
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

ANALYZER_VERSION = 1
DATA_ROOT = os.environ.get("DATA_ROOT", "/data")

app = FastAPI(title="whipradio-analysis")


class AnalyzeRequest(BaseModel):
    path: str  # relative to DATA_ROOT
    mode: str = "music"  # "music" | "speech"


@app.get("/health")
def health():
    return {
        "status": "ok",
        "service": "whipradio-analysis",
        "provider": "analysis",
        "label": f"WhipRadio Analysis v{ANALYZER_VERSION}",
        "analyzer_version": ANALYZER_VERSION,
    }


@app.post("/analyze")
def analyze(request: AnalyzeRequest):
    full_path = os.path.normpath(os.path.join(DATA_ROOT, request.path))
    if not full_path.startswith(os.path.normpath(DATA_ROOT)):
        raise HTTPException(status_code=400, detail="path escapes DATA_ROOT")
    if not os.path.isfile(full_path):
        raise HTTPException(status_code=404, detail=f"file not found: {request.path}")

    audio, sr = sf.read(full_path, dtype="float32", always_2d=True)
    mono = audio.mean(axis=1)
    duration = len(mono) / sr

    result = {
        "duration_seconds": round(duration, 3),
        "analyzer_version": ANALYZER_VERSION,
        "mode": request.mode,
    }

    # --- silence boundaries (librosa.effects.trim at top_db=40) -----------------
    _, (start_idx, end_idx) = librosa.effects.trim(mono, top_db=40)
    result["leading_silence_seconds"] = round(start_idx / sr, 3)
    result["trailing_silence_seconds"] = round(max(0.0, (len(mono) - end_idx) / sr), 3)

    # --- loudness (EBU R128) + true peak (4x oversampled) ------------------------
    meter = pyloudnorm.Meter(sr)
    try:
        lufs = float(meter.integrated_loudness(mono))
        if not np.isfinite(lufs):
            lufs = -70.0
    except ValueError:
        lufs = -70.0  # too short for a gated measurement
    result["integrated_lufs"] = round(lufs, 2)

    oversampled = librosa.resample(mono, orig_sr=sr, target_sr=sr * 4, res_type="polyphase")
    peak = float(np.max(np.abs(oversampled))) if len(oversampled) else 0.0
    result["true_peak_db"] = round(20 * np.log10(peak) if peak > 0 else -120.0, 2)

    # --- 2 Hz RMS energy profile, normalised 0..1 -------------------------------
    hop = sr // 2
    rms = librosa.feature.rms(y=mono, frame_length=hop, hop_length=hop, center=False)[0]
    rms_max = float(rms.max()) if len(rms) else 0.0
    profile = (rms / rms_max) if rms_max > 0 else rms
    result["energy_profile"] = [round(float(v), 4) for v in profile]

    if request.mode == "speech":
        result.update(
            bpm=None, bpm_confidence=0.0, beats=None,
            intro_end_seconds=None, intro_confidence=0.0,
            outro_start_seconds=None, outro_confidence=0.0,
        )
        return result

    # --- BPM + beat grid on the percussive component -----------------------------
    percussive = librosa.effects.hpss(mono)[1]
    onset_env = librosa.onset.onset_strength(y=percussive, sr=sr)
    tempo, beat_frames = librosa.beat.beat_track(onset_envelope=onset_env, sr=sr)
    bpm = float(np.atleast_1d(tempo)[0])

    tempogram = librosa.feature.tempogram(onset_envelope=onset_env, sr=sr)
    agg = tempogram.mean(axis=1)
    agg_sum = float(agg.sum())
    confidence = float(np.clip((agg.max() / agg_sum) * 25, 0, 1)) if agg_sum > 0 else 0.0

    # Octave-error guard: if the tracker landed below 70 BPM, test the double
    # tempo against the tempogram and take the stronger peak.
    if 0 < bpm < 70:
        freqs = librosa.tempo_frequencies(len(agg), sr=sr)
        idx_single = int(np.argmin(np.abs(freqs - bpm)))
        idx_double = int(np.argmin(np.abs(freqs - bpm * 2)))
        if agg[idx_double] > agg[idx_single]:
            bpm *= 2

    beats = librosa.frames_to_time(beat_frames, sr=sr)
    result["bpm"] = round(bpm, 2) if bpm > 0 else None
    result["bpm_confidence"] = round(confidence, 3)
    result["beats"] = [round(float(b), 3) for b in beats]

    # --- intro end: RMS threshold + onset cumulative cross-check ------------------
    median_rms = float(np.median(rms)) if len(rms) else 0.0
    intro_end, intro_conf = _detect_intro(rms, median_rms, onset_env, sr, duration)
    result["intro_end_seconds"] = intro_end
    result["intro_confidence"] = intro_conf

    # --- outro start: sustained energy drop ---------------------------------------
    outro_start, outro_conf = _detect_outro(rms, median_rms, duration)
    result["outro_start_seconds"] = outro_start
    result["outro_confidence"] = outro_conf

    return result


def _detect_intro(rms, median_rms, onset_env, sr, duration):
    """First time the 2 Hz RMS stays >= 55% of median for >= 2 s, cross-checked
    with the cumulative onset-strength jump; confidence from their agreement."""
    if median_rms <= 0 or len(rms) < 5:
        return None, 0.0

    threshold = 0.55 * median_rms
    sustain = 4  # 4 frames at 2 Hz = 2 s
    rms_time = None
    for i in range(len(rms) - sustain + 1):
        if np.all(rms[i : i + sustain] >= threshold):
            rms_time = i / 2.0
            break

    if rms_time is None:
        return None, 0.0

    cumulative = np.cumsum(onset_env)
    total = float(cumulative[-1]) if len(cumulative) else 0.0
    onset_time = None
    if total > 0:
        jump_idx = int(np.searchsorted(cumulative, 0.15 * total))
        onset_time = float(librosa.frames_to_time(jump_idx, sr=sr))

    if onset_time is None:
        confidence = 0.4
    else:
        confidence = float(np.clip(1.0 - abs(rms_time - onset_time) / 10.0, 0.0, 1.0))

    if rms_time >= duration * 0.9:
        return None, 0.0  # "intro" covering the whole track is no intro

    return round(rms_time, 3), round(confidence, 3)


def _detect_outro(rms, median_rms, duration):
    """Last time RMS falls below 45% of median and stays there >= 3 s."""
    if median_rms <= 0 or len(rms) < 7:
        return None, 0.0

    threshold = 0.45 * median_rms
    sustain = 6  # 6 frames at 2 Hz = 3 s
    below = rms < threshold

    # Walk backwards: the outro is the start of the final low-energy region
    # of at least `sustain` frames that reaches the end of the track.
    end = len(below)
    while end > 0 and below[end - 1]:
        end -= 1

    region_len = len(below) - end
    if region_len < sustain:
        return None, 0.0

    outro_time = end / 2.0
    if outro_time <= duration * 0.3:
        return None, 0.0  # half the track "fading" is not an outro

    confidence = float(np.clip(region_len / 20.0 + 0.4, 0.0, 1.0))
    return round(outro_time, 3), round(confidence, 3)
