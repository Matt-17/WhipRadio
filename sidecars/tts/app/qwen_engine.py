"""Qwen3-TTS engine (https://github.com/QwenLM/Qwen3-TTS).

Two-model architecture:
- Resident: a small Base model (env QWEN_TTS_MODEL, default 0.6B-Base) that
  CLONES persisted voice artifacts for everyday synthesis. ~1.5-2.5 GB VRAM.
- Transient: the 1.7B-VoiceDesign model is loaded ONLY for /design-voice calls
  and freed immediately after - voice design is rare, synthesis is constant.

Determinism rule: a designed voice is an ARTIFACT (reference WAV + transcript
under QWEN_VOICES_DIR), never re-designed per call. The voice handle is the
artifact folder name; cloning the same artifact reproduces the same voice.
"""

from __future__ import annotations

import gc
import json
import logging
import os
import re
import threading
import uuid
from pathlib import Path

import numpy as np
import soundfile as sf

from .engine_base import EngineBase

logger = logging.getLogger(__name__)

VOICES_DIR = Path(os.environ.get("QWEN_VOICES_DIR", "/models/qwen-voices"))
SYNTH_MODEL = os.environ.get("QWEN_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-Base")
DESIGN_MODEL = os.environ.get("QWEN_TTS_DESIGN_MODEL", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign")
# sdpa = PyTorch built-in fused attention: meaningfully faster than eager
# ("manual") without the flash-attn build dependency. Falls back if rejected.
ATTN_IMPL = os.environ.get("QWEN_TTS_ATTN", "sdpa")

# ITtsEngine speaks ISO codes; Qwen wants language names.
LANGUAGE_NAMES = {
    "zh": "Chinese", "en": "English", "ja": "Japanese", "ko": "Korean",
    "fr": "French", "ru": "Russian", "pt": "Portuguese",
    "es": "Spanish", "it": "Italian",
}

DESIGN_SAMPLE_TEXTS = {
    "en": "Welcome back to the show — you are listening to WhipRadio, "
          "where every song is made just for you. Stay tuned!",
}


def _model_tail(model_id: str) -> str:
    return model_id.rsplit("/", 1)[-1]


def _model_size(model_id: str) -> str | None:
    match = re.search(r"(\d+(?:\.\d+)?)B", model_id, flags=re.IGNORECASE)
    return f"{match.group(1)}B" if match else None


def _model_rate(model_id: str) -> str | None:
    match = re.search(r"(\d+(?:\.\d+)?)Hz", model_id, flags=re.IGNORECASE)
    return f"{match.group(1)}Hz" if match else None


def _language_name(code: str) -> str:
    return LANGUAGE_NAMES.get((code or "en").lower()[:2], "English")


class QwenEngine(EngineBase):
    name = "qwen"
    sample_rate = 24000  # all output is resampled to this before returning
    wants_kwargs = True  # main.py passes language/instruction through

    def __init__(self) -> None:
        self._model = None
        self._clone_prompts: dict[str, object] = {}
        # Designs are heavyweight (transient 1.7B model) and MUST be serialized:
        # concurrent designs (client retries!) would race for VRAM and OOM.
        self._design_lock = threading.Lock()
        self._synth_lock = threading.RLock()
        VOICES_DIR.mkdir(parents=True, exist_ok=True)

    # --- resident synth model -------------------------------------------------

    def _synth_model(self):
        with self._synth_lock:
            if self._model is None:
                import torch
                from qwen_tts import Qwen3TTSModel

                logger.info("Loading Qwen3-TTS synth model %s (attn=%s)", SYNTH_MODEL, ATTN_IMPL)
                self._model = self._load(SYNTH_MODEL, torch)
        return self._model

    @staticmethod
    def _load(model_id: str, torch):
        from qwen_tts import Qwen3TTSModel

        cuda = torch.cuda.is_available()
        base_kwargs = {
            "device_map": "cuda:0" if cuda else "cpu",
            "dtype": torch.bfloat16 if cuda else torch.float32,
        }
        try:
            try:
                return Qwen3TTSModel.from_pretrained(
                    model_id, attn_implementation=ATTN_IMPL, **base_kwargs)
            except (TypeError, ValueError) as exc:
                logger.warning("attn=%s rejected (%s) — loading with default attention", ATTN_IMPL, exc)
                return Qwen3TTSModel.from_pretrained(model_id, **base_kwargs)
        except torch.cuda.OutOfMemoryError:
            logger.warning("CUDA full — loading %s on CPU (slower, still fine)", model_id)
            return Qwen3TTSModel.from_pretrained(model_id, device_map="cpu", dtype=torch.float32)

    # --- engine contract --------------------------------------------------------

    def resolve_voice(self, voice: str, language: str) -> str:
        if (VOICES_DIR / voice / "ref.wav").is_file():
            return voice

        # No artifact for this handle: fall back to any designed voice rather
        # than failing the broadcast; the log makes the misconfig visible.
        existing = self.voices()
        if existing:
            fallback = existing[0]["id"]
            logger.warning("Qwen voice '%s' not found; falling back to '%s'", voice, fallback)
            return fallback

        raise ValueError(
            f"Qwen voice '{voice}' does not exist and no designed voices are available. "
            "Design one via POST /design-voice first."
        )

    def synthesize_segment(
        self, text: str, voice: str, speed: float,
        language: str = "en", instruction: str | None = None,
    ) -> np.ndarray:
        with self._synth_lock:
            model = self._synth_model()
            prompt = self._clone_prompt(model, voice)

            kwargs = {
                "text": text,
                "language": _language_name(language),
                "voice_clone_prompt": prompt,
            }
            if instruction:
                kwargs["instruct"] = instruction

            try:
                wavs, sr = model.generate_voice_clone(**kwargs)
            except TypeError:
                # this build's clone API has no instruct parameter - style comes
                # from the designed voice itself then
                kwargs.pop("instruct", None)
                wavs, sr = model.generate_voice_clone(**kwargs)

        audio = np.asarray(wavs[0] if isinstance(wavs, (list, tuple)) else wavs, dtype=np.float32)
        if audio.ndim > 1:
            audio = audio.mean(axis=-1)
        return self._resample(audio, int(sr), self.sample_rate)

    def unload(self) -> dict:
        with self._synth_lock:
            loaded = self._model is not None or bool(self._clone_prompts)
            self._model = None
            self._clone_prompts.clear()

        gc.collect()
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
                if hasattr(torch.cuda, "ipc_collect"):
                    torch.cuda.ipc_collect()
        except Exception:  # noqa: BLE001 - best-effort VRAM cleanup endpoint
            logger.exception("Qwen unload cleanup failed")

        logger.info("Unloaded Qwen resident synth model: %s", loaded)
        return {"engine": self.name, "unloaded": loaded}

    def voices(self) -> list[dict]:
        result = []
        for meta_path in sorted(VOICES_DIR.glob("*/meta.json")):
            try:
                meta = json.loads(meta_path.read_text(encoding="utf-8"))
                result.append({
                    "id": meta_path.parent.name,
                    "language": meta.get("language", "en"),
                    "gender": meta.get("gender", "?"),
                    "engine": self.name,
                    "description": meta.get("description", ""),
                })
            except (OSError, json.JSONDecodeError):
                continue
        return result

    def status(self) -> dict:
        synth_size = _model_size(SYNTH_MODEL) or _model_tail(SYNTH_MODEL)
        design_size = _model_size(DESIGN_MODEL) or _model_tail(DESIGN_MODEL)
        rate = _model_rate(SYNTH_MODEL) or _model_rate(DESIGN_MODEL)
        label_prefix = f"Qwen3-TTS {rate}" if rate else "Qwen3-TTS"
        return {
            "engine": self.name,
            "label": f"{label_prefix} - {synth_size} synth / {design_size} voice design",
            "sample_rate_hz": self.sample_rate,
            "models": {
                "synth": SYNTH_MODEL,
                "voice_design": DESIGN_MODEL,
            },
            "resident_loaded": self._model is not None,
            "designed_voices": len(self.voices()),
            "attention": ATTN_IMPL,
        }

    # --- voice design (transient 1.7B model) -------------------------------------

    def design_voice(
        self, description: str, gender: str, language: str = "en",
        sample_text: str | None = None,
    ) -> tuple[str, bytes, float]:
        """Mints a voice: returns (handle, preview wav bytes 44.1k, duration)."""
        import torch
        from qwen_tts import Qwen3TTSModel

        text = sample_text or DESIGN_SAMPLE_TEXTS.get(
            (language or "en")[:2], DESIGN_SAMPLE_TEXTS["en"])
        gender_word = "female" if (gender or "").lower().startswith("f") else "male"
        instruct = f"A {gender_word} radio host voice. {description}".strip()

        # One design at a time, period: this gates VRAM AND prevents duplicate
        # work when an impatient client retries while we are still rendering.
        with self._design_lock:
            logger.info("Designing Qwen voice (transient %s): %s", DESIGN_MODEL, instruct[:120])
            design_model = None
            try:
                design_model = self._load(DESIGN_MODEL, torch)
                wavs, sr = design_model.generate_voice_design(
                    text=text, language=_language_name(language), instruct=instruct)
            finally:
                del design_model
                gc.collect()
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()

        audio = np.asarray(wavs[0] if isinstance(wavs, (list, tuple)) else wavs, dtype=np.float32)
        if audio.ndim > 1:
            audio = audio.mean(axis=-1)

        handle = "qv-" + uuid.uuid4().hex[:10]
        folder = VOICES_DIR / handle
        folder.mkdir(parents=True, exist_ok=True)
        sf.write(folder / "ref.wav", audio, int(sr), subtype="PCM_16")
        (folder / "meta.json").write_text(json.dumps({
            "description": description,
            "instruct": instruct,
            "gender": gender_word[0],
            "language": (language or "en")[:2],
            "ref_text": text,
            "model": DESIGN_MODEL,
        }, ensure_ascii=False, indent=2), encoding="utf-8")

        preview = self._to_wav_bytes(self._resample(audio, int(sr), 44100), 44100)
        return handle, preview, len(audio) / int(sr)

    def clone_voice(self, sample_wav: bytes, ref_text: str, name: str | None = None) -> str:
        """Persists an uploaded sample as a voice artifact; returns the handle."""
        import io

        audio, sr = sf.read(io.BytesIO(sample_wav), dtype="float32", always_2d=True)
        mono = audio.mean(axis=1)

        handle = "qv-" + (re.sub(r"[^a-z0-9]+", "-", (name or "").lower()).strip("-")
                          or uuid.uuid4().hex[:10])
        folder = VOICES_DIR / handle
        folder.mkdir(parents=True, exist_ok=True)
        sf.write(folder / "ref.wav", mono, int(sr), subtype="PCM_16")
        (folder / "meta.json").write_text(json.dumps({
            "description": f"cloned: {name or handle}",
            "gender": "?",
            "language": "en",
            "ref_text": ref_text,
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        return handle

    def preview_path(self, handle: str) -> Path | None:
        path = VOICES_DIR / handle / "ref.wav"
        return path if path.is_file() else None

    # --- internals -----------------------------------------------------------------

    def _clone_prompt(self, model, handle: str):
        if handle not in self._clone_prompts:
            folder = VOICES_DIR / handle
            meta = json.loads((folder / "meta.json").read_text(encoding="utf-8"))
            self._clone_prompts[handle] = model.create_voice_clone_prompt(
                ref_audio=str(folder / "ref.wav"),
                ref_text=meta.get("ref_text", ""),
            )
        return self._clone_prompts[handle]

    @staticmethod
    def _resample(audio: np.ndarray, source_rate: int, target_rate: int) -> np.ndarray:
        if source_rate == target_rate or audio.size == 0:
            return audio
        target_length = int(round(audio.size * target_rate / source_rate))
        positions = np.linspace(0, audio.size - 1, target_length)
        return np.interp(positions, np.arange(audio.size), audio).astype(np.float32)

    @staticmethod
    def _to_wav_bytes(audio: np.ndarray, rate: int) -> bytes:
        import io

        buffer = io.BytesIO()
        sf.write(buffer, audio, rate, format="WAV", subtype="PCM_16")
        return buffer.getvalue()
