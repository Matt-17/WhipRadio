# WhipRadio — Phase 8 Sketch: Business (RSaaS)

> This is a **strategy sketch**, not an implementation plan — deliberately lighter than
> the other phase docs. It frames the decisions to make *before* building a hosted
> business, so the architecture choices in Phases 4–7 don't accidentally box you in.
> Treat everything here as options to discuss, not commitments.

---

## 1. The idea

"Radio Station as a Service" (RSaaS): someone signs up and gets their own AI radio
station in minutes — the Phase 7 onboarding, but multi-tenant and hosted. WhipRadio the
open-source project remains; the hosted offering is a separate, optional commercial layer.

---

## 2. The big architectural forks (decide early, even if you build late)

These are the choices that are expensive to reverse, so they're worth holding in mind
from Phase 4 onward:

- **Tenancy model.** Single-tenant-per-deploy (each customer = their own stack — simplest,
  maps directly to the Compose/Helm from Phase 7, easy isolation, higher per-tenant cost)
  vs multi-tenant (shared services, tenant-scoped data — cheaper at scale, much more
  engineering: data isolation, noisy-neighbour, per-tenant secrets). **Lean:** start
  single-tenant-per-deploy; it's the Phase 7 artifact with a provisioning wrapper, and it
  defers the hard multi-tenant work until demand proves it out.
- **Compute for generation.** Music/TTS GPUs are the cost center. Per-tenant dedicated GPU
  is simple but expensive; a shared generation pool with a job queue is cost-efficient but
  needs fair scheduling and isolation. This is the single biggest cost/architecture
  question for a hosted model.
- **Data store.** SQLite-per-station is fine single-tenant; multi-tenant likely wants
  Postgres (noted in Phase 7). Don't migrate until tenancy demands it.
- **Open-source vs hosted boundary.** What stays in the free OSS project vs what's
  exclusive to the hosted plan (e.g. provisioning, billing, managed scaling, premium
  voice/model providers). Decide this *before* marketing either, to avoid resentment in
  the OSS community.

---

## 3. Cost shape (the thing that makes or breaks RSaaS)

A hosted AI radio station's marginal cost is dominated by **continuous generation**, not
serving the stream. Things to model before pricing:
- GPU-hours per active station (music + TTS + LLM), and how much **off-peak/pre-generated
  content** can amortise it (the project's "produce ahead, replay goldies" design is
  genuinely a cost advantage here).
- Marginal cost of an *idle-ish* station (replaying its library, occasional talk) vs a
  *hot* one (constant new generation, big group podcasts).
- External API pass-through (OpenAI/ElevenLabs) — flat-rate vs metered to the customer.

**Implication for the product:** the existing throttle/queue design (Phase 2) and
produce-ahead (Phase 3b) are not just quality features — they're the levers that make
unit economics work. Keep them first-class.

---

## 4. Plausible packaging (illustrative, not fixed)
- **Self-host (free, OSS):** the whole project; you bring your own hardware/keys.
- **Hosted Starter:** one station, mostly pre-generated/replayed, capped fresh generation,
  CPU or shared GPU — cheap to run.
- **Hosted Pro:** more fresh generation, group podcasts, premium voices/providers,
  priority generation.
- **Managed/Enterprise:** dedicated resources, custom branding, SLAs.

This isn't financial advice — it's a sketch of where the cost tiers naturally fall. Real
pricing needs the cost model from §3 with actual measured GPU-hours.

---

## 5. Non-engineering realities to flag (not solve here)
- **Music rights:** fully AI-generated music sidesteps a lot, but if Phase 6's imported
  human library or real-world references enter a *commercial* offering, licensing matters.
- **Voice/likeness:** premium TTS (ElevenLabs) and any voice cloning carry their own terms;
  a hosted product inherits those obligations.
- **Content moderation at scale:** a hosted multi-tenant product is responsible for what
  thousands of autonomous hosts say — the moderation hooks (Phase 6 Twitch, action
  guardrails) become compliance infrastructure, not just polish.
- **Broadcast/advertising regulation:** out of scope for the project by your own call, but
  it re-enters the moment money and real ads do.

---

## 6. What to actually do in "Phase 8" (if pursued)
1. **Measure** real generation cost on representative stations (you'll have the telemetry
   from the Stats page + mixer logs).
2. **Provisioning wrapper** around the Phase 7 Compose/Helm artifact: create/start/stop/
   tear down a station from an API + a tiny control panel. (Single-tenant-per-deploy keeps
   this small.)
3. **Billing/accounts** only once provisioning is real — don't build a billing system for
   a product that can't yet spin up a station automatically.
4. **Decide and document the OSS/hosted line** publicly.
5. Revisit multi-tenancy / shared GPU pool **only** when single-tenant economics prove
   demand.

---

## 7. Open questions (for a real discussion, not the agent)
- Is the goal a sustainable side-project (self-host + a few managed instances) or a
  venture-scale product? The answer changes nearly every fork above.
- Single-tenant-per-deploy vs true multi-tenant as the starting bet?
- Where exactly is the OSS/hosted boundary?
- Are you comfortable operating GPUs / a managed cluster, or is this where a co-founder /
  managed platform comes in (ties back to the Phase 7 DevOps-light note)?

> Bottom line: nothing in Phases 3a–7 needs to commit to a business model, but a few
> choices (tenancy, data store, the produce-ahead cost levers, the OSS/hosted line) are
> worth keeping in peripheral vision so Phase 8 stays open rather than blocked.
