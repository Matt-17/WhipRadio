# WhipRadio - Phase 0 Deferred Implementation Fixes

This file tracks program-level fixes required to make the implementation match the
Phase 0 decisions. It intentionally excludes features already owned by later phase
plans, such as rich artists/bands, `VoiceProfile`, `VoiceFx`, `SegmentRenderer`, and
full generated-photography infrastructure.

## 1. Model Studio Resource Capacity Explicitly

**Problem:** the app can book one studio, but it cannot know whether two studios share
the same physical GPU. A single global semaphore is wrong for remote studios, but no
replacement exists for shared local capacity.

**Fix:**
- Add optional studio metadata such as `ResourceGroup`, `MachineName`, or
  `MaxConcurrentJobs`.
- Ensure two studios in the same resource group cannot overbook the declared capacity.
- Keep unrelated remote studios independent.
- Show active jobs and resource groups on the Studios/Admin surfaces.

**Done when:** the operator can tell WhipRadio which studios share hardware, and the app
respects that without assuming all studios are local.

## 2. Make Studio Lifecycle Control Explicitly Operator-Owned

**Problem:** timeout handling may try to restart a studio container. That is only valid
for local Docker-managed studios, not for remote machines or APIs.

**Fix:**
- Add an explicit per-studio management mode: unmanaged, local Docker, or API-managed.
- Only call Docker restart logic for local Docker-managed studios.
- For unmanaged/remote studios, mark the job failed and surface a clear health warning.

**Done when:** WhipRadio never attempts local Docker control for a remote or API studio.

## 3. Remove Manual Photo URL As A Primary Model

**Problem:** `Moderator.PhotoUrl` and the UI photo URL editor imply manual URLs/uploads.
Phase 0 now says images should come from generated-image records, with skeletons until
approved images exist.

**Fix:**
- Remove or deprecate `PhotoUrl` from DTOs and UI.
- Keep skeleton placeholders.
- When Phase 6b image entities land, reference canonical generated images instead.
- Provide a migration path for existing `PhotoUrl` data, likely dropping it unless a
  one-time import is explicitly approved.

**Done when:** no primary host/artist UI asks for a photo URL or upload path.

## 4. Add A Decision Drift Check

**Problem:** Phase 0 drifted silently while the implementation evolved.

**Fix:**
- Keep Phase 0 as a status-aware decision register.
- Add a lightweight checklist to PRs or release notes for changes that alter model,
  studio, GPU/resource, image, voice, or audio architecture decisions.
- Update `AGENTS.md` to remind contributors to revise Phase 0 and this file when those
  decisions change.

**Done when:** architecture changes have an explicit doc update or an explicit note that
no Phase 0 decision changed.

## 5. Add Manual Jingle Import Only If It Becomes A Real Need

**Problem:** Phase 3b supports generated jingles, but not uploads/imports. That is
intentional for now: generated audio keeps provenance clear and avoids introducing
media validation, loudness, and copyright handling in the identity page.

**Fix:**
- If Phase 6 or an operator workflow needs it, add a controlled import path for WAV
  jingles.
- Run imported audio through the same duration, format, loudness, and analysis checks
  used for generated station audio.
- Store imported jingles as `Jingle` records with explicit provenance.

**Done when:** operators can import vetted jingle WAVs without bypassing storage,
metadata, or audio safety checks.
