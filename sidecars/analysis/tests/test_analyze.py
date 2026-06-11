"""Sidecar analysis tests with synthetic fixtures (no committed binaries).

Run inside the container or a local venv:
    pip install -r requirements.txt pytest httpx
    pytest sidecars/analysis/tests -q
"""

import os
import tempfile
import time

import numpy as np
import soundfile as sf
from fastapi.testclient import TestClient

import app.main as main

SR = 44100


def _write(tmpdir, name, data):
    path = os.path.join(tmpdir, name)
    sf.write(path, data.astype(np.float32), SR)
    return name


def _click_track(bpm=128.0, seconds=40.0, quiet_intro=10.0):
    """Click track at the given BPM with a near-silent intro."""
    n = int(SR * seconds)
    audio = np.random.normal(0, 0.0005, n)  # noise floor
    interval = 60.0 / bpm
    t = quiet_intro
    while t < seconds:
        start = int(t * SR)
        click = np.hanning(2048) * 0.8 * np.sin(2 * np.pi * 1000 * np.arange(2048) / SR)
        end = min(start + 2048, n)
        audio[start:end] += click[: end - start]
        t += interval
    return audio


def _constant_tone(seconds=20.0):
    t = np.arange(int(SR * seconds)) / SR
    return 0.3 * np.sin(2 * np.pi * 220 * t)


def _speech_burst(seconds=8.0):
    n = int(SR * seconds)
    envelope = np.clip(np.sin(np.linspace(0, 9 * np.pi, n)), 0, 1)
    return (np.random.normal(0, 0.15, n) * envelope)


def test_analyze_click_track_bpm_intro_lufs():
    with tempfile.TemporaryDirectory() as tmp:
        main.DATA_ROOT = tmp
        client = TestClient(main.app)
        name = _write(tmp, "click.wav", _click_track())

        started = time.monotonic()
        response = client.post("/analyze", json={"path": name, "mode": "music"})
        elapsed = time.monotonic() - started

        assert response.status_code == 200
        body = response.json()
        assert body["bpm"] is not None
        assert abs(body["bpm"] - 128.0) <= 2.0 or abs(body["bpm"] - 64.0) <= 1.0
        assert body["intro_end_seconds"] is not None
        assert abs(body["intro_end_seconds"] - 10.0) <= 1.5
        assert body["duration_seconds"] == 40.0
        assert elapsed < 20.0


def test_analyze_constant_tone_lufs_accuracy():
    with tempfile.TemporaryDirectory() as tmp:
        main.DATA_ROOT = tmp
        client = TestClient(main.app)
        name = _write(tmp, "tone.wav", _constant_tone())

        body = TestClient(main.app).post("/analyze", json={"path": name, "mode": "music"}).json()

        # A -10.5 dBFS sine measures around -13.5 LUFS (K-weighting at 220 Hz);
        # assert the meter is in a sane band rather than chasing exact filters.
        assert -16.0 <= body["integrated_lufs"] <= -10.0
        assert body["trailing_silence_seconds"] < 0.5
        assert len(body["energy_profile"]) > 0


def test_analyze_speech_mode_skips_music_features():
    with tempfile.TemporaryDirectory() as tmp:
        main.DATA_ROOT = tmp
        client = TestClient(main.app)
        name = _write(tmp, "speech.wav", _speech_burst())

        body = client.post("/analyze", json={"path": name, "mode": "speech"}).json()

        assert body["bpm"] is None
        assert body["beats"] is None
        assert body["intro_end_seconds"] is None
        assert body["integrated_lufs"] < 0
        assert body["mode"] == "speech"


def test_analyze_rejects_path_escape():
    client = TestClient(main.app)
    response = client.post("/analyze", json={"path": "../../etc/passwd", "mode": "music"})
    assert response.status_code in (400, 404)
