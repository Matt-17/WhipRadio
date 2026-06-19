ARG CUDA_VERSION=12.8.1
ARG PYTHON_VERSION=3.11
ARG ACESTEP_REF=dce621408bee8c31b4fcf4811682eb9359e1bc94

FROM nvidia/cuda:${CUDA_VERSION}-runtime-ubuntu22.04

ARG PYTHON_VERSION
ARG ACESTEP_REF

ENV DEBIAN_FRONTEND=noninteractive
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        software-properties-common \
        build-essential \
        git \
        curl \
        libsndfile1 \
        libsndfile1-dev \
        ffmpeg \
        libffi-dev \
        libssl-dev \
    && add-apt-repository ppa:deadsnakes/ppa \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        python${PYTHON_VERSION} \
        python${PYTHON_VERSION}-dev \
        python${PYTHON_VERSION}-venv \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ghcr.io/astral-sh/uv:0.7 /uv /uvx /bin/

WORKDIR /app
RUN git clone https://github.com/ace-step/ACE-Step-1.5.git . \
    && git checkout "${ACESTEP_REF}"

RUN uv sync --frozen --no-dev --python python${PYTHON_VERSION}
RUN uv pip install --python .venv/bin/python --index-url https://download.pytorch.org/whl/cu128 \
        torch==2.8.0 torchvision==0.23.0 torchaudio==2.8.0

RUN mkdir -p /models/checkpoints /models/huggingface /models/cache /models/torch \
    /models/triton /models/torchinductor /models/tmp

# The API server hardcodes its checkpoint dir to {repo}/checkpoints via __file__
# (ignores ACESTEP_PROJECT_ROOT/ACESTEP_CHECKPOINTS_DIR) — without this symlink,
# model weights download into the container layer and are lost on every restart.
RUN rm -rf /app/checkpoints && ln -s /models/checkpoints /app/checkpoints

ENV ACESTEP_API_HOST=0.0.0.0
ENV ACESTEP_API_PORT=8002
ENV ACESTEP_CONFIG_PATH=acestep-v15-turbo
ENV ACESTEP_LM_MODEL_PATH=acestep-5Hz-lm-0.6B
ENV ACESTEP_LLM_BACKEND=pt
ENV ACESTEP_INIT_SERVICE=false
ENV ACESTEP_CHECKPOINTS_DIR=/models/checkpoints
ENV ACESTEP_TMPDIR=/models/tmp
ENV HF_HOME=/models/huggingface
ENV XDG_CACHE_HOME=/models/cache
ENV TORCH_HOME=/models/torch
ENV TRITON_CACHE_DIR=/models/triton
ENV TORCHINDUCTOR_CACHE_DIR=/models/torchinductor
ENV TOKENIZERS_PARALLELISM=false

VOLUME ["/models"]
EXPOSE 8002

HEALTHCHECK --start-period=600s --interval=30s --timeout=10s --retries=5 \
    CMD curl -sf "http://localhost:${ACESTEP_API_PORT}/health" > /dev/null || exit 1

# The watchdog supervises the server: a generation thread that hangs (e.g. a
# CPU VAE decode after VRAM exhaustion) bricks the queue worker while /health
# stays green — the watchdog detects the stall via /v1/stats and exits so the
# container's restart policy brings up a fresh server.
COPY watchdog.py /app/whip_watchdog.py

CMD ["uv", "run", "python", "/app/whip_watchdog.py"]
