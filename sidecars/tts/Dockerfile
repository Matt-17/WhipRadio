FROM python:3.11-slim

# espeak-ng provides the G2P fallback Kokoro/misaki uses for OOV words.
RUN apt-get update \
    && apt-get install -y --no-install-recommends espeak-ng libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

# TORCH_INDEX=cpu (default) or cu128 for NVIDIA GPU inference.
# torch AND torchaudio installed together from the same index so later
# packages (qwen-tts requires torchaudio) can't pull a mismatched build
# linked against a different CUDA runtime.
ARG TORCH_INDEX=cpu
RUN pip install --no-cache-dir torch torchaudio --index-url https://download.pytorch.org/whl/${TORCH_INDEX}

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app
COPY assets ./assets

# Model files download on first use into the mounted cache volume.
ENV HF_HOME=/models
# Qwen3-TTS: resident synth (clone) model + transient voice-design model.
# 0.6B-Base fits the 12 GB budget next to the LLM; 1.7B variants for big cards.
ENV QWEN_TTS_MODEL=Qwen/Qwen3-TTS-12Hz-0.6B-Base
ENV QWEN_TTS_DESIGN_MODEL=Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign
ENV QWEN_VOICES_DIR=/models/qwen-voices
EXPOSE 8001

# Generous start period: the first start downloads the Kokoro model.
HEALTHCHECK --start-period=300s --interval=30s --timeout=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8001/health', timeout=5)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8001"]
