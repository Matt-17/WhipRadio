# bookworm (FFmpeg 5.1): audiocraft pins av==11.0.0 (sdist only), and PyAV 11
# does not compile against the FFmpeg 7 shipped in newer Debian releases.
FROM python:3.11-slim-bookworm

# PyAV is compiled from source against the distro FFmpeg, hence the dev headers.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg libsndfile1 build-essential pkg-config \
        libavformat-dev libavcodec-dev libavdevice-dev \
        libavutil-dev libavfilter-dev libswscale-dev libswresample-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

# torch first, from the PyTorch wheel index (TORCH_INDEX=cpu default, cu121 for
# NVIDIA GPU). Everything else comes from PyPI so binary wheels (e.g. PyAV when
# wheels exist) are picked up instead of source builds.
ARG TORCH_INDEX=cpu
RUN pip install --no-cache-dir --upgrade pip \
    && pip install --no-cache-dir torch==2.1.0 torchaudio==2.1.0 --index-url https://download.pytorch.org/whl/${TORCH_INDEX}

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

# Model weights download on first generation into the mounted cache volume.
ENV HF_HOME=/models
ENV MUSICGEN_MODEL=facebook/musicgen-small
ENV ENABLE_ACESTEP=0
EXPOSE 8002

# Generous start period: dependency import alone takes a while on first boot.
HEALTHCHECK --start-period=300s --interval=30s --timeout=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8002/health', timeout=5)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8002"]
