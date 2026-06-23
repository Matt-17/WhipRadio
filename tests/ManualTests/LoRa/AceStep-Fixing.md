# ACE‑Step Integration — Fixes, Findings & LoRA Removal (WhipRadio)

Status: **Closed** — sidecar VAE‑decode fix and watchdog shipped & validated (§12, §14); artist‑voice **LoRA was removed** from the codebase.
Author: investigation + end‑to‑end validation against the running `whipradio-acestep` sidecar (container `romantic_wescoff`, API `http://localhost:8002`).
Scope: everything learned while making ACE‑Step produce songs for WhipRadio — the verified API contract, the bugs found and fixed (B1–B7), and the voice‑identity decision (`ref_audio` vs LoRA vs cover). Retained as a historical record; the LoRA‑activation plan (§5) is no longer actionable.

> **⚑ Update (2026‑06‑24): LoRA removed.** The artist‑voice LoRA provider code
> (`PrepareArtistLoraAsync`/train/preprocess/load/scale/toggle), the `AceStepLora*`
> `MusicRequest` fields, `MusicVoiceReferenceTrack`, and the `AceStepOptions`/`appsettings`
> `EnableArtistLora`/`ArtistLora*` config were all **deleted**. A single‑song LoRA never
> cloned the voice, while the spoken `ref_audio` clip transferred it well at a fraction of
> the cost. The decision rationale now also lives in `Phase-0-Tech-Decisions.md`. This file
> is kept for the verified API contract (§3) and the shipped non‑LoRA fixes (§12, §14).
> The LoRA bug deep‑dives (B1–B6) and the merge plan (§5) below are **obsolete** —
> historical reference only.

> **⚑ Outcome & status (read this first).**
> Voice identity comes from **text2music + a short *spoken* `ref_audio` clip** — the artist's ~5 s designed voice. In listening tests this transferred the voice **perfectly** and is cheap to encode (a short reference is much faster + lighter on VRAM than a full song, with no quality gain from going longer). This is **how WhipRadio works** (`ResolveVoiceReferenceAsync` → `ReferenceAudioPath` → `ref_audio` multipart). A single‑song **LoRA does not clone the voice** and was **removed**.
>
> | Item | Status |
> |---|---|
> | VAE decode tile‑overlap fix (`chunk_size=256`) — §12 | ✅ **Shipped & validated** (Dockerfile + image rebuilt + container) |
> | Container liveness / wedge recovery (watchdog + restart policy) — §14 | ✅ **Shipped & validated** (`start-studios.ps1` + container) |
> | Voice identity = short spoken `ref_audio` — §11 | ✅ **Wired in WhipRadio; confirmed, kept** |
> | Artist‑voice **LoRA** activation (B1–B6, §5 merge plan) | ❌ **Removed** — `ref_audio` chosen; LoRA provider code/config deleted |
>
> Correction to earlier drafts: the “prefer a *sung* reference over the spoken clip” idea (old §11 #2) was **retracted** — the short *spoken* clip tested perfect and is faster, so we keep it.

---

## 1. Goal

Make WhipRadio actually use a per‑artist ACE‑Step **LoRA adapter** during music generation, so that an artist keeps a recognizable musical/vocal identity across songs — instead of the LoRA code existing but never being exercised.

The full LoRA lifecycle is:

```
scan → preprocess → train → export → load → scale → toggle(on) → GENERATE
```

This is validated to work end‑to‑end against the live sidecar (see §3); a real 3:27 song was produced with a trained adapter loaded and active. What is missing is the wiring, two correctness bugs, and the infra/path plumbing described below.

---

## 2. Current state (what already exists)

| Piece | Location | State |
|---|---|---|
| LoRA lifecycle implementation | `src/WhipRadio.Infrastructure/Music/AceStepGenerationProvider.cs` (`PrepareArtistLoraAsync`, `TrainArtistLoraAsync`, `TryLoadAndActivateLoraAsync`) | **Implemented**, gated behind `EnableArtistLora` |
| Config surface | `src/WhipRadio.Infrastructure/Music/AceStepOptions.cs` + `appsettings.json` `AceStep` section | **Present**; `EnableArtistLora: false` |
| Request fields | `src/WhipRadio.Core/Abstractions/IMusicGenerator.cs` (`AceStepLora*`, `MusicVoiceReferenceTrack`) | **Present** |
| Producer that fills those fields | `src/WhipRadio.Orchestrator/Services/MusicProductionService.cs` | **Missing** — never sets any `AceStepLora*` field |
| Sidecar endpoints | ACE‑Step image | **Working** (verified) |
| Shared volume for datasets | `docker inspect` → only `/dein/models → /models` | **Insufficient** for LoRA datasets |

Net: even if you flip `EnableArtistLora=true` today, `PrepareArtistLoraAsync` hits the guard at `AceStepGenerationProvider.cs:167‑171` (no references / null paths) and immediately calls `TryUnloadLoraAsync` — **no LoRA is ever applied.**

---

## 3. Verified endpoint contract (from live testing)

These were confirmed by driving the real sidecar; the plan and the bug fixes depend on them.

- **All JSON responses are enveloped**: `{ data, code, error, timestamp }`. FastAPI *errors* instead return `{ "detail": ... }` with HTTP 4xx.
- **Generation is NOT `/v1/chat/completions`.** It is:
  `POST /release_task` → `data.task_id`; poll `POST /query_result {task_id_list:[id]}` → `data[0].status` (`0`=running, `1`=done, `2`=failed); on done, `data[0].result` is a **JSON string** whose `[0].file` is a relative URL `"/v1/audio?path=<urlencoded>"`. `GET {ApiBase}{file}` returns the WAV. This already matches the C# provider.
- **Preprocess done** = `data.status == "completed"`, **but success requires `data.result.num_tensors >= 1`** (see Bug B2).
- **Training done** = `data.is_training == false` AND `data.status` contains `complete`/`saved` (it is a human string, never `"done"`). Failure shows as `data.error` or a status containing `No valid`/`Failed`/`Error`.
- **Export creates a nested folder**: `POST /v1/training/export {export_path, lora_output_dir}` does `copytree(lora_output_dir/final → export_path)`, and `final/` itself contains an `adapter/` subdir → PEFT files land at **`export_path/adapter/`** (`adapter_config.json`, `adapter_model.safetensors`). See Bug B1.
- **Activation requires three calls**: `/v1/lora/load` → `/v1/lora/scale` → `/v1/lora/toggle {use_lora:true}`. Load alone does **not** activate (status `use_lora:false`).
- **Hardware reality**: 12 GB GPU, CPU‑offload mode active (only ~3.5 GB free with model resident). This causes Bug B2 and the decode stall in B7.
- **Adapter reuse survives restarts**: the exported PEFT dir lives on disk under the shared volume, so after a container restart you can `load` it again without retraining (the in‑process LoRA state is lost on restart, the files are not). **Verified live.** This is what makes per‑artist adapter caching viable (load‑before‑train).

A reusable manual harness for all of this lives at `tests/ManualTests/LoRa/Test-AceStepLoRA.ps1` (scan→…→load→generate→download, with the fixes baked in). Use it to validate each merge step.

---

## 4. Gaps & bugs (what blocks activation)

> **Status:** **B7 is ✅ shipped** (the decode/VRAM fix — see §12). **B1–B6 are ⏸ deferred** — they only matter if the LoRA path is activated, which the project chose not to do (voice comes from `ref_audio`). Kept here as a verified reference for if/when LoRA is revisited.

### B1 — Export/Load adapter‑path mismatch (**will fail 100% when enabled**)
`TrainArtistLoraAsync` exports with `export_path = AceStepLoraAdapterPath` (`AceStepGenerationProvider.cs:284`), then `TryLoadAndActivateLoraAsync` loads `lora_path = AceStepLoraAdapterPath` (`:189/:197/:410`). But export puts the PEFT files in `AdapterPath/adapter`, so load fails:
`❌ Invalid adapter: expected PEFT LoRA directory containing adapter_config.json ... in <AdapterPath>`.
**Verified live.**

**Fix:** load from the directory that actually contains `adapter_config.json`. Deterministic for the LoRA path is `<AdapterPath>/adapter`; make it resilient by probing:
- try `lora_path = AdapterPath`; on “Invalid adapter / not found”, retry `lora_path = AdapterPath + "/adapter"`; **or**
- after export, resolve the real dir (the sidecar response `data.source` + known `/adapter` nesting) and store that as the load path.

### B2 — Preprocess can “complete” with **zero tensors** (cold‑DiT device bug)
Right after model init (cold DiT), preprocessing throws inside the sidecar:
`RuntimeError: Expected all tensors to be on the same device, but got mat2 is on cpu, different from other tensors on cuda:0` (`preprocess_encoder.py:34`). The status still returns `completed` but `result.num_tensors == 0`; training then dies with `❌ No valid samples found in tensor directory`. **Verified live** (failed cold; succeeded once the model was warm).

Root cause: the offload context (`init_service_offload_context.py:19‑33`) only moves the DiT to GPU **if its first parameter is on CPU**; load‑time `model.to(device)` can miss submodules, leaving `model.encoder` on CPU while the body is on CUDA. `PollPreprocessAsync` does not inspect `num_tensors`.

**Fix (two layers):**
- *Defensive (C#, ship first):* extend `PreprocessStatusData` with `result.num_tensors`; in `PollPreprocessAsync` treat `completed && num_tensors==0` as failure; warm the DiT (one short throwaway generation) and retry once.
- *Root cause (sidecar image):* in `sidecars/acestep/Dockerfile`, patch `_load_model_context` to always run `_recursive_to_device(model, device)` for `model_name=="model"` (not just when the first param is on CPU), or pin an ACE‑Step build that already does. This removes the flakiness for everyone.

### B3 — Producer not wired (**core gap**)
`MusicProductionService` builds `MusicRequest` (`:264`, `:292`) and sets `ReferenceAudioPath`/`ReferenceAudioLabel` but **none** of `AceStepLoraReferences / AceStepLoraDatasetPath / AceStepLoraTensorPath / AceStepLoraTrainingOutputPath / AceStepLoraAdapterPath / AceStepLoraActivationTag`. Without these, the provider guard skips LoRA entirely.

**Fix:** add an artist‑LoRA resolver (mirror `ArtistVoiceReferenceResolver`) that, for the artist:
1. selects training songs (the artist’s own approved/upvoted tracks from the library — `RadioOptions.TracksDirectory`),
2. materializes them (+ matching `*.caption.txt`/lyrics) into a **container‑visible** dataset dir,
3. returns `MusicVoiceReferenceTrack[]` (each `FileName` must match a file in the dataset dir — see `LabelDatasetSamplesAsync` matching at `:294`),
4. computes per‑artist `dataset / tensor / trainingOutput / adapter` paths and the activation tag,
5. these get assigned onto the `MusicRequest`.

### B4 — Path visibility / shared volume (**infra**)
The ACE‑Step container only mounts `/dein/models → /models`. The orchestrator’s `DataRoot` (Windows dev: `<cwd>/data`) is **not** visible to the container. The LoRA `dataset/scan audio_dir`, `preprocess output_dir`, `training lora_output_dir`, `export export_path`, and `lora/load lora_path` are all read **by path inside the container** (unlike `ReferenceAudioPath`, which is uploaded via multipart and so may be a host path).

**Fix:** give the orchestrator and the sidecar a shared LoRA workspace and pass **container** paths:
- add a bind mount (reuse `/models`, e.g. host `…/models/whipradio-lora` → container `/models/whipradio-lora`, or add a dedicated `/data` mount), and
- add an `AceStep:ContainerDataRoot` (host→container) translation option so the resolver emits `/models/whipradio-lora/<artistId>/...` paths while writing to the corresponding host dir.

### B5 — Inline latency & training cadence (**architecture / UX**)
`PrepareArtistLoraAsync` runs **inline inside `GenerateAsync`**, before the song. With `ArtistLoraTrainEpochs=10` + 30/90‑min preprocess/training timeouts, the **first** song per artist blocks for a long time. (`TryLoadAndActivateLoraAsync` is tried first, so subsequent songs reuse the adapter and are fast.)

**Recommendation:** train the adapter out‑of‑band, mirroring the existing `ArtistMemberVoiceQueue` + `ArtistMemberVoicePreparationService` background pattern, and have `GenerateAsync` only **load+activate** an already‑exported adapter (skip LoRA when not yet trained). Decide before merge (see §8).

### B6 — `auto_label` is broken in the current image (not on the critical path)
`POST /v1/dataset/auto_label` (and the async variant) fail with `LabelAllMixin.label_all_samples() got an unexpected keyword argument 'chunk_size'`. **Verified live.** WhipRadio does **not** depend on this — the C# path self‑labels via `PUT /v1/dataset/sample/{idx}` (`LabelDatasetSamplesAsync`), which works. Implication: WhipRadio must always supply its own captions/lyrics (it does); do not plan to lean on the sidecar’s ASR/auto‑captioning until the image is fixed. If transcription is ever wanted, fix it in the image build.

### B7 — VRAM headroom: don’t keep the 5Hz LM resident during decode (**operational**)
On the 12 GB card in CPU‑offload mode, **a long VAE decode stalls to a near‑halt when the 5Hz LM is pinned in VRAM alongside the DiT + LoRA.** Observed live: with the LM resident, free VRAM fell to ~310 MB and a 207 s decode thrashed for 9+ minutes without finishing; after a restart with **only the DiT initialized** (LM left to load transiently and offload), free VRAM was ~5.8 GB and the same 207 s song rendered in **~8 s**. Guidance for the orchestrator: do not force‑init the LM for generation, ensure the LM is offloaded before the decode tile loop, and keep enough free VRAM for the decode. This also argues for the background‑training cadence in B5 (training + a hot LM + generation should not contend for VRAM simultaneously).

*Related decode‑quality fix:* on a ≤12 GB GPU the auto VAE decode `chunk_size` is 128 while the tile `overlap` defaults to 64, so `chunk_size − 2·overlap == 0`; the decoder force‑reduces the crossfade to 32 and logs `[tiled_decode] Reduced overlap from 64 to 32 …` **every song** (benign, but it surfaces into WhipRadio's progress/logs). `sidecars/acestep/Dockerfile` now sets `ACESTEP_VAE_DECODE_CHUNK_SIZE=256`, which keeps the full 64‑frame crossfade and larger tiles (smoother audio, no warning). Needs decode‑time VRAM headroom (hence “don’t pin the LM”); the decoder has OOM fallbacks, and the value can be lowered to 192 if those fallbacks ever trigger. **This fix is shipped, rebuilt, and validated — see §12.**

---

## 5. Merge plan (incremental, each step independently mergeable) — ⏸ DEFERRED

> **Deferred:** this is the **LoRA activation** plan. The project chose `ref_audio` for voice identity, so this is **not being executed** — kept verbatim as the playbook for if/when persistent musical‑style LoRA is wanted. The two non‑LoRA fixes that came out of this investigation (decode §12, liveness §14) are shipped independently.
>
> Order chosen so the pipeline is *correct* before it is *enabled*. The flag stays `false` until Step 6.

**Step 0 — Bring the harness in (done / reference).**
`tests/ManualTests/LoRa/Test-AceStepLoRA.ps1` already exercises the corrected contract. Keep as the manual acceptance test.

**Step 1 — Fix B1 (adapter load path).**
In `AceStepGenerationProvider.cs`, make `TryLoadAndActivateLoraAsync` resolve/probe `<AdapterPath>/adapter`. Add a unit test in `tests/WhipRadio.Infrastructure.Tests/AceStepGenerationProviderTests.cs` for the nested‑path resolution. *Low risk, no behavior change while flag is off.*

**Step 2 — Fix B2 (zero‑tensor guard + warm‑up retry).**
Extend `PreprocessStatusData` with `Result.NumTensors`; fail `PollPreprocessAsync` on `completed && num_tensors==0`; add a one‑shot warm‑up generation + single retry. Unit‑test the status parsing.

**Step 3 — Fix B2 root cause in the image (optional but recommended).**
Patch `_load_model_context` in `sidecars/acestep/Dockerfile`; rebuild via `build-sidecars.ps1`. Verify cold‑start preprocess yields `num_tensors>=1` using the harness immediately after `/v1/init`.

**Step 4 — Infra B4 (shared LoRA workspace).**
Add the bind mount + `AceStep:ContainerDataRoot` translation. Document in `sidecars/acestep/README.md`. Verify the container can `ls` a file the orchestrator wrote.

**Step 5 — Wire the producer B3.**
Add `ArtistLoraReferenceResolver` (new) + integrate into `MusicProductionService`; populate the six `AceStepLora*` request fields only when references exist and the workspace is configured. Cover with orchestrator tests (mirror `ArtistMemberVoiceBootstrapTests`). Keep `EnableArtistLora=false` — code is dormant but exercised by tests.

**Step 6 — Decide cadence B5, then enable.**
Implement inline‑vs‑background per §8. Then flip `appsettings.json` `AceStep:EnableArtistLora=true` (consider environment‑scoped enablement first).

**Step 7 — End‑to‑end acceptance.**
Generate a *vocal* song for an artist with ≥1 reference; confirm `/v1/lora/status` shows `lora_loaded/use_lora=true, active_adapter=<artist>` during the run, the song downloads as valid WAV, and the adapter is reused (fast) on the next song.

---

## 6. Config changes (`appsettings.json` → `AceStep`)

- `EnableArtistLora`: `false → true` (Step 6, ideally environment‑gated).
- Confirm training strength: `ArtistLoraRank=32`, `ArtistLoraAlpha=64`, `ArtistLoraTrainEpochs=10` (already set — far stronger than the rank‑4/1‑epoch smoke test, appropriate for identity capture).
- `ArtistLoraScale=0.75`: identity nudge without overpowering the prompt. Tune 0.6–0.9 by ear.
- New: `ContainerDataRoot` (host→container path map for the shared workspace).

---

## 7. How to verify each step

Use the harness against a warm container:
```powershell
cd tests\ManualTests\LoRa
.\Test-AceStepLoRA.ps1 -WavFile .\test.wav -SongSeconds 207 -KeepArtifacts
```
It asserts: scan finds labeled samples → `num_tensors>=1` → training completes (`is_training=false`) → export → load from the **nested** adapter dir → scale+toggle → `lora_loaded/use_lora=true` → 3:27 WAV downloaded and RIFF/WAVE‑validated. The bug fixes in Steps 1–2 mirror exactly what the harness does.

---

## 8. Open decisions (need an answer before Step 6)

1. **Training data source.** Which audio trains an artist’s LoRA — the artist’s own *approved/upvoted generated songs* (recommended; `MusicVoiceReferenceTrack` already carries up/down votes), a curated seed set, or the spoken voice‑design previews (not ideal — those are speech, not songs)?
2. **Cadence.** Inline on first generation (simple, but a long first‑song stall) vs. background queue (recommended, matches the voice‑prep pattern). 
3. **Minimum references & retrain trigger.** `ArtistLoraMinReferenceTracks` (currently 1 — fine for a smoke test, likely too few for identity). When to retrain (e.g., after N new approved songs)?
4. **Instrumental/jingle handling.** Already correctly skipped (`PrepareArtistLoraAsync` unloads LoRA for jingles/instrumental/no‑vocals) — confirm that is the desired policy.
5. **LoRA vs. spoken‑voice reference (or both).** See §11 — decide whether voice identity comes from the LoRA, the already‑wired TTS spoken‑voice `ref_audio`, a sung reference, or a combination. This affects how much of this plan is even needed up front.

---

## 9. Note on “the test song didn’t sound like the source”

During validation, a song generated with the smoke‑test adapter sounded like instrumental techno while the source was a Latin vocal track. That was **expected** and is **not** a LoRA failure: the smoke test deliberately used a generic caption, `all_instrumental=true`, empty lyrics, and an *electronic‑instrumental generation prompt*. ACE‑Step output style is driven primarily by the **prompt + lyrics**; a rank‑4/1‑epoch LoRA from a single clip only nudges timbre — it does not replay the reference. The production path is already better (`LabelDatasetSamplesAsync` labels `IsInstrumental:false`, passes real lyrics, and builds vocal‑aware captions; config trains rank‑32/10‑epochs). For faithful identity continuity, the key levers are: real per‑artist song references, sufficient rank/epochs, accurate captions, and **generation prompts/lyrics that match the artist’s genre**.

**This was then confirmed empirically.** Re‑running the pipeline against the *same* `test.wav` but labeled correctly — `is_instrumental:false`, `language:es`, a latin/vocal caption, Spanish lyrics, rank‑32 / 8‑epoch training, scale 0.85, and a matching latin generation prompt **with lyrics** — produced a 3:27 latin **vocal** song (`tests/ManualTests/LoRa/lora-song-latin-3m27.wav`, 207 s, B♭ minor, 95 BPM). Same source audio, opposite result from the techno‑instrumental smoke test — which proves the earlier mismatch was purely labeling/prompt configuration, not a LoRA limitation. Takeaway for WhipRadio: the production self‑labeling and prompt construction must stay faithful to each artist’s actual genre/vocal style.

---

## 10. Validation log (this investigation)

Two full end‑to‑end runs against the live sidecar, both producing valid 3:27 WAVs:

| Run | Labeling | Generation prompt / lyrics | Result |
|---|---|---|---|
| Smoke test | LoRA: `electronic instrumental`, `all_instrumental=true`, no lyrics, rank‑4/1‑epoch | electronic‑instrumental prompt, no lyrics | techno **instrumental** — didn’t resemble the latin vocal source (expected; see §9) |
| Latin vocal (LoRA) | LoRA: `latin pop`, `is_instrumental=false`, `es`, Spanish lyrics, rank‑32/8‑epoch | latin prompt **+ Spanish lyrics**, scale 0.85 | latin **vocal** song; but male voice (prompt didn’t state gender) and voice not a clone |
| Latin female (LoRA) | same LoRA | latin prompt + **“female lead vocals”** + denser lyrics | sang lyrics, female‑ish — but **voice still clearly different** from source |
| Salsa female (LoRA) | same LoRA | **salsa** prompt + female + lyrics | mostly vamped (“lalala”) — salsa arrangements are instrumental‑dominant; latin‑pop prompts sing better |
| **Reference audio** (no LoRA) | — | latin female prompt + lyrics + **`test.wav` uploaded as `ref_audio`** | **voice transfer worked well** (user‑confirmed) → §11 conclusion |
| Reference audio (spoken) | — | same, but **`voice.wav`** (5.4 s spoken clip) as `ref_audio` | production‑realistic TTS case; spoken clip transfers weaker than the sung one |

Bugs/behaviors confirmed during these runs feed §3–§4 and §12: B1 (nested adapter export), B2 (cold‑DiT zero tensors; fixed by warming the DiT before preprocess), B6 (`auto_label` `chunk_size` crash), B7 (LM‑resident decode stall + the tile‑overlap fix), and adapter reuse across restart. The reusable harness is `tests/ManualTests/LoRa/Test-AceStepLoRA.ps1`.

---

## 11. Design decision: artist LoRA vs. TTS spoken‑voice reference

ACE‑Step exposes **three distinct** mechanisms — don't conflate them:

- **text2music + `ref_audio` (voice conditioning):** generate a *new* song while conditioning the voice/timbre toward an uploaded reference clip (`ref_audio` multipart on `/release_task`; absolute `reference_audio_path` is rejected). No training; per‑generation; this is what the TTS spoken voice feeds. Cheapest lever for "same voice, new song." **Verified live** with `test.wav` as the reference.
- **LoRA:** fine‑tunes the *model* on an artist's songs → persistent identity (style + voice) across *all* generations, no reference needed each call. Heavier; needs song data + the B1–B7 fixes; one song can't clone a voice. **Not** a cover tool.
- **Cover mode (`cover_noise_strength` 0→1, `cover_mode`/`task_type`):** audio‑to‑audio that regenerates a *specific source track* (keeps melody/structure). This is the actual "cover song" tool — separate from LoRA.

WhipRadio's voice‑identity question is really about the first two (the third is for covers). They are **complementary** — ACE‑Step can use a loaded LoRA *and* `ref_audio` at once (LoRA biases the model; `ref_audio` conditions the individual generation).

**A. TTS spoken‑voice reference — already wired and live.**
`ArtistMemberVoicePreparationService` designs a per‑member spoken voice (qwen TTS) → `VoiceReferencePath`; `MusicProductionService.ResolveVoiceReferenceAsync` → `MusicRequest.ReferenceAudioPath` → `AceStepGenerationProvider.CreateTaskAsync` uploads it as multipart `ref_audio` on `/release_task`. **Runs on every vocal generation and does NOT depend on `EnableArtistLora`.**
- Pros: zero training; already implemented; gender‑correct (designed per member); per‑song acoustic conditioning; no VRAM/training cost.
- Cons: conditions a *sung* output with a *spoken* clip → domain gap, so voice transfer is partial and variable; gives no persistent musical‑style identity.

**B. Artist LoRA — this plan.**
Fine‑tunes the DiT on the artist’s actual **songs** → persistent musical + vocal identity baked into the model.
- Pros: strongest, reusable identity across all generations; captures musical style, not just timbre.
- Cons: needs song training data, training time/VRAM, and the B1–B7 fixes; weak from a single clip; does not pin vocal gender by itself (the prompt does).

**Recommendation (settled by listening tests):**
1. **Use the artist's short *spoken* designed voice clip as `ref_audio`.** ~5 s transferred the voice **perfectly**. Already wired in WhipRadio (`ResolveVoiceReferenceAsync` → `ReferenceAudioPath`), gender‑correct, and kept as‑is.
2. **Keep the reference short — do *not* reuse a full prior song.** The reference is VAE‑encoded, so cost scales with its *duration*: a 5 s clip is ~50–60× cheaper to encode than a 3–4 min song → faster + much less VRAM (a long reference risks the §B7 decode thrash), with **no quality gain**. *(This retracts an earlier "prefer a sung reference" draft — a full‑song reference was implemented and then reverted after testing.)*
3. **Always state vocal gender/style in the generation prompt.** The model defaults to a male voice if gender is omitted (that's what produced a male voice from a female source in testing). WhipRadio's `AceStepPromptBuilder` already emits "female/male lead vocals" from `VocalGender` — keep it.
4. **LoRA stays deferred** (§ status table) — `ref_audio` already nails "same voice." Revisit LoRA only for persistent *musical style* across a catalog, not for voice.

---

## 12. Shipped: VAE decode tile‑overlap fix (rebuilt & validated)

**Symptom (surfaced in WhipRadio logs):** every song logged `[tiled_decode] Reduced overlap from 64 to 32 for chunk_size=128`.

**Cause:** the tiled VAE decoder requires `chunk_size − 2·overlap > 0`. On a ≤12 GB GPU `_get_auto_decode_chunk_size()` returns **128** while `overlap` defaults to **64**, so `128 − 128 == 0` (degenerate); the decoder force‑halves the crossfade to 32 and warns. Benign (it self‑corrects) but it hides a quality loss and spams logs. `chunk_size` is env‑configurable; `overlap` is not.

**Fix (quality‑first):** `sidecars/acestep/Dockerfile` now sets `ENV ACESTEP_VAE_DECODE_CHUNK_SIZE=256`. With `chunk_size=256` the full **64‑frame** crossfade is valid (`256 − 128 = 128 > 0`) → larger tiles + smoother blending → higher quality, and the warning never fires.

**Validated end‑to‑end** on the rebuilt image (original code, env‑driven):
- `[tiled_decode] chunk_size=256` confirmed; **0** “Reduced overlap” warnings; **0** OOM/CPU‑fallbacks.
- Decode peak VRAM **8.93 GB** on the 12 GB card (comfortable headroom); song rendered and saved.
- Validation artifact: `tests/ManualTests/LoRa/acestep-decode256-validation.wav`.

**Operational note (recreate ⇒ one‑time model re‑download):** the `whipradio-acestep` container had the 5Hz LM cached in its **writable layer**, not under the `/models` bind. Recreating the container (needed to apply the new env) dropped it, forcing a one‑time ~1.33 GB re‑download — which now lands in the persistent `/models/checkpoints/acestep-5Hz-lm-0.6B`, so subsequent recreates won’t repeat it. Lesson: model caches should live under `/models`; verify HF/checkpoint caches persist there so container rebuilds are cheap.

---

## 13. Artifacts produced (historical — not retained)

The validation audio and the LoRA pipeline harness (`Test-AceStepLoRA.ps1`) were
working artifacts during the investigation and have since been removed along with the
LoRA code. They are listed here only to describe what each run demonstrated.

| File | What it demonstrated |
|---|---|
| `test.wav` | Source: female Latin vocal song (the identity target) |
| `voice.wav` | Short 5.4 s spoken clip (TTS‑style reference) |
| `lora-song-latin-3m27.wav` | LoRA, latin vibe, sang lyrics — but **male** (prompt omitted gender) |
| `lora-song-latin-female.wav` | LoRA + “female” prompt + dense lyrics — sings, but voice not a clone |
| `lora-song-salsa-female.wav` | Salsa prompt — mostly vamped (instrumental‑dominant) |
| `ref-audio-latin-female.wav` | **`ref_audio`=`test.wav`, no LoRA — the approach that worked** |
| `ref-voicewav-latin-female.wav` | `ref_audio`=`voice.wav` (spoken) — production‑realistic case |
| `acestep-decode256-validation.wav` | VAE decode fix (chunk_size=256) validation song |

---

## 14. Shipped: container liveness / wedge recovery (self‑supervising)

**The risk:** the sidecar runs generations on a single queue‑worker thread. A hung job (e.g. a CPU VAE decode after VRAM exhaustion) **bricks the queue while `/health` stays green**, so every later job blocks forever. The Docker `HEALTHCHECK` cannot catch this (health is green during a wedge).

**The mechanism — the container supervises itself (single responsibility, no orchestrator/AppHost needed, so it survives a published deployment):**
- The image `CMD` is **`whip_watchdog.py`** (PID 1). It spawns `api_server` as a child and polls `/v1/stats` every 30 s. If jobs are **pending but none reach a terminal state for `ACESTEP_STUCK_TIMEOUT_SECONDS`**, it kills the server and exits.
- The container runs with **`--restart unless-stopped`** (set by `start-studios.ps1` → `Ensure-Container`), so the watchdog's exit brings up a **fresh** server. That is the whole recovery loop, contained entirely in the sidecar.

**Layered timeouts (and the invariant):**
| Knob | Where | Value | Meaning |
|---|---|---|---|
| `ACESTEP_GENERATION_TIMEOUT` | sidecar env | **600 s (10 min)** | hard cap for **one song**; a longer generation is aborted → job `failed` (a terminal state), so a slow song never looks like a wedge |
| `ACESTEP_STUCK_TIMEOUT_SECONDS` | watchdog env | **1200 s (20 min)** | wedge detector → kill + container restart |
| `GenerationTimeout` | orchestrator `appsettings` | 45 min | the app's own patience on a request (outer bound) |

> **Invariant: `STUCK` must be > `GENERATION_TIMEOUT`.** Otherwise the watchdog would kill a *legitimately* long‑running single generation (a running job keeps the terminal count unchanged for its whole duration). The defaults (10 min / 20 min) honor this; `start-studios.ps1` now sets exactly these (it previously inflated them to 30/40 min).

**The orchestrator's role is now just job handling, not liveness.** `StudioMusicGenerator` still catches `TimeoutException` and calls `StudioDockerControl.TryRestartAsync` (a `docker restart`), and the studios page has a manual restart button — but with the watchdog active these are **secondary**: a published orchestrator may not even have Docker access, and the container already self‑heals at 20 min. Treat the app‑side restart as a dev convenience + operator button, not the safety net.

**Applied:** `start-studios.ps1` (the operator launcher, which creates `whip-studio-acestep-N` with the watchdog + `--restart unless-stopped`) now uses 10/20‑min timeouts. The ad‑hoc dev container we tested against (`romantic_wescoff`, port 8002) was running **without** the watchdog and with `--restart no`; it has been recreated with the watchdog CMD + `--restart unless-stopped` + the same timeouts, so it is now self‑supervising too. Verified: PID 1 = `whip_watchdog.py` supervising `api_server`, log `stuck timeout 1200s`, restart policy `unless-stopped`.
