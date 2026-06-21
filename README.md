# WhipRadio

> Llamas whipped the radio's mix.

A fully local, AI-driven internet radio station orchestrated with **.NET Aspire**:
locally generated music, AI moderators with distinct personas, Kokoro/Piper TTS,
Open-Meteo weather reports, and a continuous MP3 stream via Icecast. It plays in
Winamp/VLC and in the built-in Blazor web app.

## Architecture

```text
AppHost (Aspire)
|-- icecast       MP3 streaming server (:8000/radio.mp3)
|-- orchestrator  .NET workers: music production, announcements, show runner,
|                 ffmpeg playout to Icecast, API for the web app
`-- web           Blazor Server broadcast console

Studio services (start-studios.ps1)
|-- writer-room   Ollama + gemma4:e4b (:11434)
|-- tts           Python/FastAPI + Kokoro/Piper (:8201)
|-- acestep       ACE-Step 1.5 official API (:8101)
`-- analysis      audio analysis API (:8301)
```

Audio pipeline: one long-lived ffmpeg encoder pushes MP3 to Icecast. Each
playlist item is decoded to raw PCM by a short-lived ffmpeg process and piped
into the encoder.

## Prerequisites

- Docker Desktop with Linux containers
- .NET SDK 10.0.3xx, see `global.json`
- ffmpeg on PATH for development
- Disk space for model caches. Gemma 4 E4B and ACE-Step download large weights on
  first use.
- Optional NVIDIA GPU. `start-studios.ps1` auto-detects `nvidia-smi` and starts
  GPU-capable studio containers with `--gpus=all`.

## Quickstart

```bash
.\start-studios.ps1
.\start.ps1
```

Open the Aspire dashboard URL printed in the AppHost console. `start-studios.ps1`
starts the long-lived AI services first; they survive WhipRadio app restarts.

| What | Where |
|---|---|
| Web app | `web` endpoint on the dashboard |
| Direct stream | http://localhost:8000/radio.mp3 |
| Icecast status | http://localhost:8000 |
| Orchestrator API | `orchestrator` endpoint, for example `/api/nowplaying` |

First start takes a while because local models download on demand. Use
`.\test-studios.ps1` to verify Ollama, Gemma 4, ACE-Step, TTS, and analysis.

Useful studio commands:

| Command | Purpose |
|---|---|
| `.\start-studios.ps1` | start Writer Room, recording, voice, and analysis services |
| `.\stop-studios.ps1` | stop studio containers without deleting model volumes |
| `.\restart-studios.ps1` | restart the studio layer |
| `.\test-studios.ps1` | probe endpoints and run a small Gemma 4 chat test |

## Configuration

Key settings:

| Key | Default | Meaning |
|---|---|---|
| `Llm__Endpoint` | `http://localhost:11434` | Ollama Writer Room endpoint |
| `Llm__Model` | `gemma4:e4b` | Ollama chat model |
| `Llm__ContextSize` | `16384` | Ollama `num_ctx` working context |
| `Weather__Latitude/Longitude` | 51.05 / 13.74 | Open-Meteo location |
| `Music__ProducerBackoffSeconds` | 30 | music production retry/backoff |
| `AceStep__Model` | `acestep-v15-turbo` | ACE-Step DiT model |
| `AceStep__Thinking` | `true` | use ACE-Step LM planning |
| `AceStep__InferenceSteps` | `12` | ACE-Step diffusion steps |
| `AceStep__GenerationTimeout` | `00:30:00` | ACE-Step generation timeout |
| `Stream__Bitrate` | `192k` | MP3 bitrate |
| `Stream__DisplayLatencySeconds` | `5` | delay now-playing/title updates to match listener stream latency |
| `Stream__EncoderInitialBackoffSeconds` | `5` | first encoder-restart backoff; doubles per rapid crash up to the cap |
| `Stream__EncoderMaxBackoffSeconds` | `60` | cap for encoder-restart backoff |
| `Stream__EncoderCrashThreshold` | `5` | circuit breaker: park the station after this many encoder crashes in the window |
| `Stream__EncoderCrashWindowMinutes` | `5` | rolling window for the encoder crash circuit breaker |
| `Icecast__SourcePassword` | _required_ | Icecast source push password; set via `.env` / `ICECAST_SOURCE_PASSWORD` |
| `Radio__DataRoot` | `/data` or `./data` | tracks, announcements, SQLite |

### Secrets

Icecast passwords and any API keys are **never committed**. Copy `.env.example`
to `.env` (gitignored) and fill in the values — the Aspire AppHost loads it on
every platform (Windows / Linux / macOS / WSL) and seeds the environment for
itself, the Orchestrator, the Web app, and the Icecast container. Real
environment variables always override `.env`, so CI/production can inject
secrets without a file. Required keys: `ICECAST_SOURCE_PASSWORD`,
`ICECAST_ADMIN_PASSWORD`, `ICECAST_RELAY_PASSWORD`.

Station-level settings live in the database and are editable in the web app.
New stations default generated tracks to 150-480 seconds.
`DefaultMusicProvider` accepts `musicgen`, `ace-step`, or `ace-step-1.5`; values
are normalized to `musicgen` or `ace-step-1.5`.

## Local music providers

### MusicGen

- Existing instrumental provider.
- Runs in the `music` Docker resource.
- Uses AudioCraft/MusicGen and keeps the existing model and continuation
  behavior for tracks longer than MusicGen's native window.
- Generated tracks are stored with `Backend = "musicgen"`.

### ACE-Step 1.5

- Complete local song generation for instrumentals or vocals.
- Supports automatic lyrics or provided lyrics.
- Runs in the separate `acestep` Docker resource.
- Uses the official ACE-Step async REST API directly.
- First generation downloads large model weights into the `acestep-models`
  volume mounted at `/models`.
- CPU execution is supported. NVIDIA GPU execution is supported when the
  container receives GPU access.
- Generated tracks are stored with `Backend = "ace-step-1.5"`.

Build the ACE-Step image:

```bash
docker build -t whipradio-acestep sidecars/acestep
```

Run on CPU:

```bash
docker run --rm \
  -p 8002:8002 \
  -v acestep-models:/models \
  whipradio-acestep
```

Run with NVIDIA GPU:

```bash
docker run --rm \
  --gpus all \
  -p 8002:8002 \
  -v acestep-models:/models \
  whipradio-acestep
```

Choose the provider through the Settings/Admin page or by setting
`StationSettings.DefaultMusicProvider`.

## Tests

```bash
dotnet test WhipRadio.slnx
```

## CI / Images

- `ci.yml`: build and test on every push/PR.
- `docker-publish.yml`: manual `workflow_dispatch`; builds and pushes
  `ghcr.io/<repo>/whip-radio-{orchestrator,web,tts,music,acestep}:latest`.

## Manual Smoke Test

1. Start the Aspire AppHost.
2. Verify `music` is healthy.
3. Verify `acestep` is healthy.
4. Select ACE-Step 1.5 as the music provider.
5. Submit or wait for one short instrumental generation.
6. Submit or wait for one short vocal generation with automatic lyrics.
7. Verify the generated file is valid WAV.
8. Verify `Track.Backend == "ace-step-1.5"`.
9. Switch the provider back to MusicGen.
10. Verify MusicGen still generates successfully.

## Troubleshooting

- No sound for several minutes after first start: model downloads and CPU music
  generation take time.
- Stream stalls in the browser: the PlayoutService reconnects to Icecast within
  a few seconds if the mount drops.
- Local host voices sound wrong: check the host's configured TTS engine and voice
  on the Hosts page.
- `acestep` is unavailable: check the `acestep` resource logs and model download
  progress.
- `ffmpeg` not found on Windows: install it and restart the terminal, or set
  `Stream__FfmpegPath`.
