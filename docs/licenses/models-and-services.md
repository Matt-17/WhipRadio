# AI Models and External Service Terms

Last reviewed: 2026-07-04

Sources used:

- `src/WhipRadio.Orchestrator/appsettings.json`
- `start-studios.ps1`
- sidecar Dockerfiles and sidecar code
- upstream model cards, project licenses, and service terms

Model weights and hosted APIs are not covered by the license of the wrapper library that calls them. Track them separately.

## Local Model Runtimes and Weights

| Model/service | Where used | Current id/config | License or terms |
| --- | --- | --- | --- |
| Ollama runtime | Writer Room local LLM server | `ollama/ollama:latest` | MIT for the runtime. Model licenses are separate. |
| Gemma | Writer Room default model | `gemma4:e4b` | Gemma 4 is documented under Apache-2.0. Older Gemma generations are covered by Gemma Terms of Use, so verify the exact Ollama tag before release. |
| nomic-embed-text | Participant-memory embeddings (Phase 5) on the Writer Room Ollama endpoint | `nomic-embed-text` (v1.5) | Apache-2.0 per the upstream model card. |
| ACE-Step 1.5 | Main music recording studio | `acestep-v15-turbo`, `acestep-5Hz-lm-0.6B` | ACE-Step 1.5 project is MIT. Its LM model line is based on Qwen3; keep upstream notices. |
| MusicGen | Optional legacy/secondary music sidecar | `facebook/musicgen-small` | Code is MIT, but the model weights are `CC-BY-NC-4.0`. This is non-commercial. |
| Kokoro | Local TTS sidecar | `hexgrad/Kokoro-82M` through `kokoro` package | Apache-2.0 for model/package according to upstream metadata. |
| Piper voices | Local TTS sidecar | `rhasspy/piper-voices` | Model repository is MIT, but the installed `piper-tts` package is `GPL-3.0-or-later`. |
| Qwen3-TTS clone model | Local TTS sidecar | `Qwen/Qwen3-TTS-12Hz-0.6B-Base` | Apache-2.0. |
| Qwen3-TTS voice design model | Local TTS sidecar | `Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign` | Apache-2.0. |

## Hosted APIs

| Service | Where used | Terms notes |
| --- | --- | --- |
| ElevenLabs | Optional TTS and music provider | Proprietary service terms. Free use is non-commercial; paid plans allow commercial use subject to the service terms and prohibited-use policy. Voice cloning inputs require rights/consent. |
| Open-Meteo | Weather source | Free API is non-commercial with rate limits. Data is provided under CC-BY 4.0; paid API plans allow commercial use subject to subscription terms. |
| OpenAI-compatible endpoint | Optional text provider path | No SDK package is currently referenced, but any configured hosted model/API must be tracked here with its service terms before production use. |

## Generated Media Notes

- AI-generated song, speech, and script output can have terms that differ from the model or API package license.
- Keep source prompts, model ids, provider, and generation timestamp in the database where possible. This gives later release/reuse reviews enough evidence to determine what terms applied.
- For cloned or reference voices, track consent and source audio rights. This is separate from the TTS model license.

## Reference Links

- Ollama license: <https://github.com/ollama/ollama/blob/main/LICENSE>
- Gemma Terms of Use: <https://ai.google.dev/gemma/terms>
- Gemma 4 Apache-2.0 license page: <https://ai.google.dev/gemma/apache_2>
- nomic-embed-text model card: <https://huggingface.co/nomic-ai/nomic-embed-text-v1.5>
- ACE-Step 1.5 license: <https://github.com/ace-step/ACE-Step-1.5/blob/main/LICENSE>
- MusicGen small model card: <https://huggingface.co/facebook/musicgen-small>
- Kokoro model card: <https://huggingface.co/hexgrad/Kokoro-82M>
- Piper voices model card: <https://huggingface.co/rhasspy/piper-voices>
- Qwen3-TTS 0.6B Base model card: <https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base>
- Qwen3-TTS 1.7B VoiceDesign model card: <https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign>
- ElevenLabs terms: <https://elevenlabs.io/terms-of-use>
- Open-Meteo terms: <https://open-meteo.com/en/terms>
