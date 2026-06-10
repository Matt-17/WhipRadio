FROM python:3.11-slim

# espeak-ng provides the G2P fallback Kokoro/misaki uses for OOV words.
RUN apt-get update \
    && apt-get install -y --no-install-recommends espeak-ng libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app
COPY assets ./assets

# Model files download on first use into the mounted cache volume.
ENV HF_HOME=/models
EXPOSE 8001

# Generous start period: the first start downloads the Kokoro model.
HEALTHCHECK --start-period=300s --interval=30s --timeout=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8001/health', timeout=5)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8001"]
