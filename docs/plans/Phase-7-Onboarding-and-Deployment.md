# WhipRadio — Phase 7 Brief: Onboarding & Deployment

> Design brief. Two halves: a **director-led onboarding** that turns an empty install
> into a living station, and the **deployment hardening** to ship it (homelab → cloud).
> The deployment half is where the user has flagged limited DevOps experience — so this
> brief leans toward safe, well-trodden choices and explains the *why*.

---

## PART A — Onboarding (the CEO/Program Director experience)

### Goal
A first-run flow where the **Program Director greets you**, collects the essentials, and
then offers to *set up the whole station itself* — programming, hosts, bands, first
songs, voice samples, jingle, slogan, frequency.

### Flow (firm shape, wording open)
1. **Detect empty station** (no formats, no hosts, no tracks) → enter onboarding instead
   of normal operation. Reuse the model-download/readiness gating from Phase 2's M6 so
   onboarding can show setup progress.
2. **Director greeting** in the Chat page (Phase 4 is a prerequisite — onboarding is a
   guided chat, which is elegant and reuses everything).
3. **Collect basics:** station name, purpose/vibe, language(s), maybe a target audience.
   Minimal typing; the director asks, you answer.
4. **Director offers full setup.** On accept, it runs a sequenced plan using existing
   capabilities (all already actions/services by Phase 4/5):
   - propose a weekly programme (Program Director from 3b/3c),
   - hire an initial host roster (`StelleHostEin`), with gender-correct voice samples you
     can audition (host preview from Phase 2),
   - ask hosts to "discover" a few bands/artists (Phase 5 generation),
   - have those artists generate the first tracks (background music production),
   - generate a station slogan + first jingle (3b branding) and set frequency,
   - write all of it to the Branding page.
5. **Progress + control:** onboarding shows live progress (console/notifications) and lets
   you accept/tweak/redo each proposal. Nothing is irreversible.
6. **Handoff:** when enough exists to sound like a station, the director says "wir sind on
   air" and normal operation begins.

**Firm rule:** onboarding orchestrates *existing* services/actions — it is a guided
sequence, not a parallel implementation. If a step needs something new, that something is
a reusable capability, not onboarding-only code.

**Open Choice:** chat-driven (recommended — reuses Phase 4, feels on-brand) vs a
classic wizard UI. A hybrid is fine: chat narration + a few structured inputs (name,
frequency) where typing is faster than conversation.

**Open Choice:** how much the director does autonomously vs asks. Recommend "proposes,
you confirm" for the first run, with a "just do it all" express option.

---

## PART B — Deployment hardening

### Goal
From "runs via Aspire on my dev box" to "deploys reliably" — homelab first, cloud as a
stretch, with an eye toward the Phase 8 RSaaS idea.

### B.1 Reality check on Aspire → containers
.NET Aspire is primarily an *orchestration/dev-time* model. For deployment you generally
want it to **emit a deployment artifact** (it can generate a manifest; tooling exists to
turn that into Docker Compose or Kubernetes). **Firm guidance:** target **Docker Compose
first** — it matches the homelab goal, is the least new DevOps to learn, and you already
build all images (Phase 1 M8/M9). Kubernetes is Part B.4, optional.

### B.2 The hard parts (be honest about these)
- **GPU access in containers:** music/TTS want a GPU. This means NVIDIA Container Toolkit
  on the host and GPU device requests in Compose/K8s. Document a clean **CPU-only profile**
  (slower, per the project's "talk fills gaps" tolerance) so a GPU-less host still works.
- **Model & data volumes:** large model caches and the growing library/DB must be **named
  volumes** (or host mounts) that persist across restarts. Make these explicit and
  documented; a fresh deploy should download models once into a persistent cache.
- **Secrets:** API keys (OpenAI, ElevenLabs, Twitch) move from DB/appsettings to
  environment/secret files for deployment. Keep the in-app settings UX, but read from a
  secret store in production.
- **Icecast exposure:** the one port that must be reachable for listeners; everything else
  can stay internal.

### B.3 Compose deliverables (firm)
- A production `docker-compose.yml` (or a small set with overrides:
  `compose.yml` + `compose.gpu.yml` + `compose.cpu.yml`).
- Pinned image tags from GHCR (Phase 1 M9 already publishes there).
- Health checks + restart policies on every service.
- A `.env.example` documenting every required variable.
- A **one-command quickstart** for a fresh Linux box (the project's original north star:
  "few commands to a running station").

### B.4 Kubernetes (optional / stretch)
Only if the homelab Compose path is solid. Keep it boring:
- A **Helm chart** (or Kustomize) mirroring the Compose topology.
- PersistentVolumeClaims for models/data; a Secret for keys; a Service/Ingress for
  Icecast + web.
- GPU scheduling via node selectors/resource requests; document a CPU-only values file.
- **Firm guidance for a DevOps-light maintainer:** prefer a managed Kubernetes if going to
  cloud (less to operate), and don't hand-roll cluster ops — lean on the chart + managed
  control plane. This is the on-ramp to Phase 8's "spin up a station in seconds".

### B.5 Hardening checklist (firm)
- Structured logging + the existing console page; sane log levels in production.
- Graceful shutdown (finish/flush current item, close Icecast source cleanly).
- Resource ceilings so a runaway generation can't starve the stream.
- Backup story for the SQLite DB + library (a documented volume snapshot is enough at
  homelab scale; note Postgres as a Phase 8 multi-tenant consideration).
- Security pass: no default passwords in production, Icecast admin locked down, secrets
  not logged.

---

## Suggested milestone spine (agent refines)
1. Empty-station detection + onboarding entry + director greeting (chat-driven).
2. Basics collection + "offer full setup" + sequenced autonomous setup over existing
   actions, with accept/tweak/redo and live progress.
3. Production Docker Compose (gpu/cpu overrides), pinned GHCR tags, health/restart,
   `.env.example`, one-command quickstart, README.
4. Hardening checklist (shutdown, limits, secrets, backup, security).
5. (Optional) Helm chart mirroring Compose, CPU/GPU values, managed-K8s notes.

---

## Definition of Done (themes)
- [ ] Fresh install → director greets, collects basics, and (on accept) stands up a
      believable station end-to-end with your confirmations
- [ ] Onboarding only orchestrates existing services/actions
- [ ] `docker compose up` on a clean Linux box yields a running, listenable station with
      one documented command (GPU and CPU profiles both work)
- [ ] Models/data persist across restarts; keys come from secrets, not the DB, in prod
- [ ] Graceful shutdown, resource ceilings, no default prod passwords, documented backup
- [ ] (If attempted) Helm deploy reaches parity with Compose on a managed cluster

---

## Open questions
- Onboarding: pure chat, wizard, or hybrid (recommend hybrid)?
- How autonomous is first-run setup (propose-and-confirm vs express "do it all")?
- Deployment target priority: confirm homelab-Compose-first, cloud/K8s as stretch?
- DB: stay SQLite for single-station; introduce Postgres only when Phase 8 multi-tenancy
  is real?
- GPU assumptions for the default published images (CPU-safe default + GPU opt-in)?
