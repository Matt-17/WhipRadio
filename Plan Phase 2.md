# WhipRadio — Phase 2: Remaining Items

> Reconciliation audit (2026-06-10): all 50 indexed issues from live testing were
> addressed; the milestones M1–M8 are implemented and verified on the running
> station (97 unit tests green). What follows is the **only** work still open,
> with the reason it was deferred. Items implemented differently than written
> are listed under "Accepted deviations" — they work as desired and need no action
> unless we change our mind.

---

## Open items

### 1. Music model selection in admin (was M4.5)
Admin/Settings should list available music backends and models (MusicGen
small/medium/large), persist the choice (`MusicGenModel` setting) and pass a
`model` field to the sidecar's `/generate`. **Why deferred:** switching models
invalidates the sidecar's loaded model and multiplies the HF cache (~2 GB per
model); needs an unload/reload path in the sidecar first. Today the model is
fixed via the `MUSICGEN_MODEL` env var (AppHost-level).

### 2. Model download progress UI + ready gating (was M6.2)
A startup sequencer that checks Ollama/`/api/tags`, pulls missing models with a
progress broadcast, and holds ShowRunner until everything is ready. **Why
deferred:** downloads already happen automatically at runtime (Ollama model
resource, HF caches in the sidecars) and the Console page shows the logs; the
missing part is only the *progress bar* UX and the explicit `ReadyToStream`
gate. The cold-start filler-talk loop currently absorbs the wait gracefully.

### 3. GPU offload / context knobs (was M6.3)
`GpuLayerCount` (Ollama `num_gpu`) and `OllamaContextSize` settings plus a
`device` field for the music sidecar. **Why deferred:** changing these requires
restarting the Ollama container / reloading sidecar models — a config-reload
channel to the containers doesn't exist yet. GPU is auto-detected and used
end-to-end already, so the practical value is low until multi-GPU or VRAM
pressure becomes real.

### 4. Per-use-case AI providers (was M5.4)
Separate `TextProvider` / `LyricsProvider` / `ReasoningProvider` settings with a
factory. **Today:** one `TextProvider` switch (ollama/openai) routes *all* text
generation. **Why deferred:** until a second provider is actually in use, three
switches are dead configuration. The router (`TextGenerationRouter`) is built so
adding per-use-case resolution is a small change.

### 5. Schedule & format pages — interaction depth (was M3.3/M3.4)
Open: click a schedule cell → side panel with format detail/director reasoning/
disable button; genre colour-coding; format **edit** (override host/genre/
duration) and per-format history. **Today:** the grid renders the full week with
tooltips, formats page has vote/toggle and the director reacts to both. The rest
is UI polish, not architecture.

### 6. Stats extensions (was M7.1)
Open: 7×24 play heatmap, uptime, longest/shortest tracks, recently retired,
hours per format, schedule coverage %, listener user-agent details. The current
stats page covers the core numbers; these are additive queries.

### 7. On-demand generation for empty formats (was M7.3)
When a format has zero matching tracks, the selector currently falls back to any
genre instead of generating a matching track synchronously ("we're making
something special…"). Needs a budget/announcement flow so a 2–10 min generation
doesn't silence the station.

### 8. Smaller follow-ups
- Talk-kind weights are hard-coded probabilities; the plan wanted them
  configurable as JSON in `StationSettings`.
- Per-host `UseBreath`: breath is a station setting + force-off for Piper.
- API-key validation on save (test call to ElevenLabs/OpenAI with success/error
  feedback); keys are stored in SQLite **unencrypted** (accepted TODO).
- ElevenLabs stability/similarity per host are not stored.
- `ProgramDirectorLog` table — director decisions go to the console log only.
- Director plan parsing has no dedicated unit test (it lives in the Orchestrator
  project, which has no test project yet); the sanitizer logic is covered
  indirectly. An Orchestrator test project would also let us test the
  Icecast-metadata client with a mock handler.
- Console page: 3 s polling without level filters (plan wanted SignalR push +
  filter buttons + autoscroll toggle).
- Language enforcement is prompt-level + logging; the "detect wrong language and
  regenerate once" check is not implemented.
- **Multilingual guests** (hosts/guests speaking a language other than the
  station language) are explicitly future work — today the station language is
  the single main language and all hosts are aligned to it automatically.

---

## Accepted deviations (working as desired — no action planned)

- **Elapsed-seconds ticking** is computed client-side from `StartedAtUtc` (1 s
  timer) instead of a server `BroadcastProgress` every second — same UX, no
  network chatter.
- **Vote baseline**: rendered as a 10-vote neutral baseline in the `VoteBar`
  component instead of seeding every track with 5/5 fake votes — keeps the
  retire rule and stats honest.
- **Day memory** is a `ModeratorMemory` table (one row per talk, queried by day)
  instead of a JSON blob column — simpler to trim and query.
- **Host rotation**: formats own their hosts; the fallback rotation runs 2-hour
  shifts. The `MaxSameHostMinutes` hard guard was dropped — forcing a host swap
  mid-format would contradict the format model.
- **Production throttle**: implemented as `TargetQueueLength` (unplayed stock) +
  `MaxLibrarySize` (hard cap) + producer backoff instead of the planned interval
  fields — simpler and verified to stop overproduction.
- **Director cadence**: continuous 10-minute cycles with an admin **Run now**
  button instead of a 03:00 daily job — reacts faster to disabled formats and
  fills the week incrementally.
- **Off air** streams silence and keeps the mount up; now-playing clears and the
  ON AIR lamp goes dark within ~2 seconds (verified live).
- **Weather** airs **once per hour, on the full hour** (prepared in the last 10
  minutes, aired in the first talk slot after the top of the hour) — replaces
  the old "every 4th cycle" rule per updated requirement.
