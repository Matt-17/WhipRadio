# WhipRadio — Phase 6b Plan: Photography (Band & Host Portraits)

> Design brief with firm decisions where they matter. This phase is, by the user's own
> framing, "technically unnecessary but breathes enormous life into the station." It adds
> AI-generated, mood-rich, DSLR-like photography for hosts and bands — for chat avatars,
> presentation cards, the schedule, artist pages, everywhere a face belongs.
>
> Sits late (6b) because it's pure polish and the **lowest VRAM priority** of any
> generation workload. Depends on the rich biographies from Phase 5 and the branding
> surfaces from Phase 3b.

---

## 1. Goal

Every host and every band/artist (and each band *member*) gets believable, consistent
portrait photography that looks shot on a DSLR — warm, intentional, human. Images are
generated **on demand, never in real time**, queued at the lowest priority, and stored
permanently. The UI shows a skeleton placeholder until a real image exists.

---

## 2. Model decision (firm)

- **FLUX.2 Klein 4B**, quantized (GGUF Q4/Q5) to fit the 12 GB RTX 4070, Apache 2.0
  licensed (matters for Phase 8). Frontier-level photorealism, and — critically —
  **multi-reference identity preservation** (up to ~10 reference images) so the same
  person stays recognisable across many photos.
- Runs behind the same sidecar pattern as TTS/music; nothing about the model choice leaks
  into orchestrator calling code (`IImageGenerationService` interface).
- **Not a real-time path.** A single 1024px image on a quantized 12 GB setup takes tens of
  seconds; that's fine — photos are produced when entities are created, then cached.

---

## 3. The VRAM rule (firmest part — this is why it's 6b)

Image generation is the **lowest-priority** GPU workload and **must never co-reside** with
the LLM, TTS-synthesis, or music generation. The quantized Klein 4B needs roughly the
whole card.

**Scheduling contract:**
1. Image jobs accumulate in a **persistent queue** (a big backlog is expected and fine).
2. The image worker only runs when the GPU has been **idle of studio work for a cooldown
   window** (config, e.g. no LLM/TTS/music job for N seconds) — i.e. "when no studios are
   needed for a while."
3. To run a batch: **unload everything else** (evict LLM, TTS, pause music generation) →
   load FLUX.2 → drain a chunk of the queue → unload FLUX.2 → reload LLM + TTS so the
   station stays responsive.
4. The live stream must **never** stall for image work — the station keeps playing from
   its library/queue while images render in the background. If the LLM is briefly evicted,
   produce-ahead announcement pooling (Phase 3b) covers the gap.
5. Until an entity's image exists, the UI shows a **skeleton placeholder** (firm UX rule).

**Open Choice:** batch size per wake-up (drain 1 vs many). Recommend draining several per
wake-up since the load/unload of FLUX.2 is the expensive part — amortise it. Agent tunes.

---

## 4. The hard part: biography-grounded, consistent photos

This is where the real design work is, and the user flagged it correctly as tricky.

### 4.1 Every photo is grounded in the biography
- **Firm rule:** the prompt builder **always** injects the entity's biography facts. A
  host photo uses `Gender, Age, Origin, Style/Persona`; a band photo uses member count,
  each member's `Gender/Age/Role/Interests`, the band's `Genre/SubGenre/Origin/Backstory`.
- Reuse the Phase 3b `PromptContextBuilder` philosophy: one `PhotoPromptBuilder` assembles
  a DSLR-style prompt from structured biography, so a "32 y/o indie host from Seattle" never
  comes out as a generic stock face. Style descriptors lean photographic: lens/bokeh,
  lighting (golden hour, studio softbox), film grain, documentary/candid framing.

### 4.2 Band photos require per-member photos FIRST (firm sequence)
The user's key insight: a band photo can't be a single blind generation — the members
must be consistent with their *own* portraits. So the pipeline is ordered:

1. **Per-member portrait first.** For each `BandMember`, generate an individual portrait
   grounded in that member's biography. Store it as the member's canonical reference.
2. **Validate/curate** (see 4.3) — a member's reference must be "good" before it's used
   downstream.
3. **Group/band photo second**, using each member's reference portrait as a
   **multi-reference input** to FLUX.2, so the band shot shows *those same people*. Match
   member count exactly; respect roles (the drummer behind a kit, etc., as far as the
   model allows).
4. **Scene variants** (studio, live, candid, press) reuse the same references for
   continuity across the artist page.

**Firm rule:** never generate a band photo before all member references exist and pass
curation. The job graph enforces this dependency (member jobs are prerequisites of the
band job).

### 4.3 Consistency & "does it match?" — the genuinely tricky bit
Identity drift is the risk. Mitigations (recommended, agent refines):
- Pin a **seed** per entity and store it, so regeneration is reproducible.
- Use FLUX.2 **multi-reference** aggressively: once a member has a good portrait, *every*
  later image of them passes it as reference.
- **Curation gate:** generated images land in a `Pending` state; an admin (or, later, an
  automatic similarity check) approves the canonical one. Cheap version for 6b: a small
  review UI on the host/artist page — generate a few candidates, pick the keeper.
- **Open Choice:** automatic identity-consistency scoring (face-embedding similarity
  between a member's reference and a group photo) to auto-reject mismatches. Recommend
  noting it as a refinement; start with human curation — it's reliable and simple.

---

## 5. Data model (migration `Phase6b`)

`GeneratedImage`:
`Id, OwnerType (Host | Artist | BandMember), OwnerId, Kind (Portrait | Group | Studio |
Live | Candid | Press | ChatAvatar), FilePath, Prompt, Seed, ReferenceImageIds (json),
Status (Queued | Rendering | Pending | Approved | Rejected), IsCanonical, CreatedAt`.

`ImageJob` (the queue):
`Id, OwnerType, OwnerId, Kind, DependsOnJobIds (json), Priority (always lowest),
Status, Attempts, CreatedAt, StartedAt, FinishedAt, Error?`.

- `BandMember` / `Moderator` / `Artist` gain a `CanonicalImageId?` (the face used as the
  reference + shown on cards/chat).
- Store originals; derive sized thumbnails for chat avatars / cards on the fly or at save.

---

## 6. Sidecar (`sidecars/image`, port 8003)

```
GET  /health  -> { "status": "ok", "model": "flux2-klein-4b", "loaded": false }
POST /load     -> loads the model (explicit, so the orchestrator controls residency)
POST /unload   -> frees VRAM
POST /generate
  body: { prompt, negative_prompt?, reference_images?[] (base64 or paths),
          seed, width, height, steps }   # Klein 4B ~4 steps
  resp: image/jpeg (or png)
```
- Model files (weights, VAE, text encoder) cached in a persistent volume; first run
  downloads them. Document ~60 GB free storage + the FP8/GGUF choice for 12 GB.
- `/load` and `/unload` are explicit because **the orchestrator owns the residency dance**
  (§3) — it unloads LLM/TTS, calls `/load`, drains, calls `/unload`, reloads LLM/TTS.

---

## 7. Orchestrator / C#

- `IImageGenerationService` (HTTP client) + `IImageJobQueue` (persistent).
- `ImageProductionService` (BackgroundService): the lowest-priority worker implementing
  the §3 scheduling contract (idle cooldown, evict-others, load, drain batch, unload,
  reload). Shares the global generation semaphore with LLM/TTS/music so nothing else
  spikes while FLUX.2 is resident.
- `PhotoPromptBuilder`: biography → DSLR prompt (per Kind).
- Dependency-aware dispatch: a `Group` job is only eligible once all member `Portrait`
  jobs are `Approved`.
- Hooks: creating a host/artist enqueues its portrait job(s); creating a band enqueues
  member portraits → then the group job (dependency).

---

## 8. UI

- **Skeleton placeholders** everywhere a face shows until an `Approved` image exists
  (chat avatars, presentation cards, schedule, artist/host pages).
- Host & Artist pages: an image section — generated candidates, pick canonical, regenerate
  (re-enqueues at lowest priority), see scene variants.
- Band/Artist page: member roster each with their portrait; the group photo; a note if the
  group photo is still blocked on a missing/También unapproved member reference.
- Admin: image queue view (depth, current job, last renders), a "render now" override that
  forces a wake-up (with the explicit warning that it will evict LLM/TTS briefly).

---

## 9. Suggested milestone spine (agent refines)
1. `sidecars/image` with FLUX.2 Klein 4B (quantized), `/load`/`/unload`/`/generate`,
   model cache volume.
2. `IImageGenerationService` + `GeneratedImage`/`ImageJob` model + persistent queue.
3. `ImageProductionService` scheduling contract (idle cooldown, evict/reload dance,
   semaphore), lowest priority, never stalls stream.
4. `PhotoPromptBuilder` (biography-grounded) + per-Kind prompt styles.
5. Member-portrait-first dependency graph; band group photo via multi-reference.
6. Curation gate + canonical selection; skeleton placeholders + UI sections.
7. (Optional) automatic identity-similarity scoring as a refinement.

---

## 10. Definition of Done (themes)
- [ ] FLUX.2 Klein 4B runs quantized on the 4070 behind `IImageGenerationService`
- [ ] Image work is lowest priority, never co-resident with LLM/TTS/music, never stalls
      the live stream; LLM/TTS reload cleanly after a batch
- [ ] Persistent queue handles a large backlog; skeletons shown until images exist
- [ ] Every photo prompt is grounded in the entity's biography
- [ ] Band photos are generated only after all member portraits exist and are approved,
      and use them as references so the same people appear
- [ ] Same entity stays recognisable across portrait/studio/live/candid via seed +
      multi-reference
- [ ] Canonical image drives chat avatar + presentation card + schedule + pages
- [ ] Regeneration is reproducible (stored seed) and re-enqueues at lowest priority

---

## 11. Open questions
- Batch size per wake-up (recommend several, to amortise load/unload).
- Human curation only, or add automatic face-similarity scoring (recommend human first).
- How hard to enforce role-accurate group staging (drummer/kit etc.) vs accept the
  model's best effort?
- Chat avatars: crop from the canonical portrait, or a dedicated tighter-framed Kind?
- Storage/quotas as the library of people grows (ties to Phase 7/8 deployment).
