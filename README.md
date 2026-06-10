# WhipRadio 📻🦙

> *Llamas whipped the radio's mix.*

A fully local, AI-driven internet radio station orchestrated with **.NET Aspire**:
locally generated music (MusicGen, optional ACE-Step vocals), AI moderators with
distinct personas (two-stage LLM pipeline via Ollama/gemma3), Kokoro TTS,
Open-Meteo weather reports, and a continuous MP3 stream via Icecast — playable in
Winamp/VLC **and** in the built-in Blazor web app.

## Architecture

```
AppHost (Aspire)
├── ollama        gemma3:4b — ScriptWriter + VoiceDirector + titles/lyrics
├── icecast       MP3 streaming server (:8000/radio.mp3)
├── tts           Python/FastAPI + Kokoro (:8001)
├── music         Python/FastAPI + MusicGen / ACE-Step (:8002)
├── orchestrator  .NET workers: music & announcement production, show runner,
│                 ffmpeg playout → Icecast; minimal API for the web app
└── web           Blazor Server broadcast console (player, library, votes, …)
```

Audio pipeline: one long-lived ffmpeg encoder pushes MP3 to Icecast; each
playlist item (track/announcement WAV) is decoded to raw PCM by a short-lived
ffmpeg and piped into the encoder — a gapless, CD-like stream.

## Prerequisites

- **Docker Desktop** (Linux containers)
- **.NET SDK 10.0.3xx** (see `global.json`)
- **ffmpeg on PATH** (dev host; e.g. `winget install Gyan.FFmpeg`)
- ~**10 GB disk** for models (gemma3:4b ≈ 3.3 GB, musicgen-small ≈ 2 GB, Kokoro ≈ 0.4 GB, images)
- Optional: NVIDIA GPU — **auto-detected**: when `nvidia-smi` is present the
  AppHost runs Ollama with `--gpus=all` and builds/runs both Python sidecars
  with CUDA torch (`TORCH_INDEX=cu121` build arg). Disable with
  `Gpu__Disabled=true`. Everything also runs on CPU, just slowly (the station
  fills the gaps with talk)

## Quickstart

```bash
dotnet run --project src/WhipRadio.AppHost
```

Open the Aspire dashboard URL printed in the console. When all resources are
healthy:

| What | Where |
|---|---|
| Web app (player, library, votes) | `web` endpoint on the dashboard |
| Direct stream (Winamp/VLC) | http://localhost:8000/radio.mp3 |
| Icecast status | http://localhost:8000 |
| Orchestrator API | `orchestrator` endpoint, e.g. `/api/nowplaying` |

**First start takes a while**: Ollama pulls gemma3:4b, the sidecars download
their models on first use. The station starts talking ("warming up the studio")
before the first track finishes generating — that's by design.

## Configuration

Key settings (env vars / `appsettings.json` of the Orchestrator):

| Key | Default | Meaning |
|---|---|---|
| `Llm__Model` | `gemma3:4b` | Ollama chat model |
| `Weather__Latitude/Longitude` | 51.05 / 13.74 (Dresden) | Open-Meteo location |
| `Music__TrackDurationSeconds` | 90 | generated track length |
| `Stream__Bitrate` | 192k | MP3 bitrate |
| `Icecast__SourcePassword` | `hackme-dev` | dev-only default |
| `Radio__DataRoot` | `/data` or `./data` | tracks, announcements, SQLite |
| `ENABLE_ACESTEP` (music sidecar) | `0` | opt-in vocal generation |

Station-level settings (name, language, queue length, announcement frequency)
live in the database and are editable on the web app's **Settings** page.

## Tests

```bash
dotnet test WhipRadio.slnx
```

## CI / Images

- `ci.yml` — build + test on every push/PR.
- `docker-publish.yml` — manual (`workflow_dispatch`); builds and pushes all four
  images to GHCR as `ghcr.io/<repo>/whip-radio-{orchestrator,web,tts,music}:latest`.

## Troubleshooting

- **No sound for several minutes after first start** — model downloads + first
  CPU music generation take time. Watch the `music`/`tts` logs in the dashboard;
  the moderators will fill the silence as soon as LLM + TTS are up.
- **Stream stalls in the browser** — the encoder bridges queue gaps with
  silence; if the mount dropped entirely, the PlayoutService reconnects within
  ~5 s. Press Listen again.
- **German moderators speak English voices** — Kokoro has no German voices;
  the TTS sidecar falls back to English and logs a warning (swap `ITtsEngine`
  backends to change this).
- **`ace-step` shows unavailable** — intentional: heavy vocal backend is off by
  default; the station produces instrumental tracks only.
- **Windows: `ffmpeg` not found** — install it and restart the terminal so the
  AppHost inherits the updated PATH, or set `Stream__FfmpegPath`.

## Repository layout

See `Plan.md` for the full Phase-1 implementation plan this repo follows
(naming: WhipRadio instead of LlamaRadio).
