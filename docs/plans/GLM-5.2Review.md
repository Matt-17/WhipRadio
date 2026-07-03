Grounded in what I actually read (PlayoutService, AudioMixerEngine, ServiceDefaults/Extensions, PlayoutRecoveryService, the test list, and the catch/throw counts), here are the next steps I'd prioritize to make WhipRadio a reliable 24/7 product. Ordered by impact-to-effort.

STATUS (updated 2026-06-26): #1, #2, #3, #4, #5, #6, #8 (secrets half) are DONE and merged to master working tree. #7, #8 (deployment artifact), #9, #10 remain TODO. #6 (Postgres cutover) is code-complete and building; its runtime verification is pending a running Docker daemon. See the "Done — implemented as" notes under each completed item for the exact files and verification results.

    1. Test the real-time audio path — it's the single biggest reliability gap
    The mixer is the riskiest code in the repo: a 761-line state machine with envelopes, beat-aligned fades, IntroTalkOver composites, timed interrupts, and early-EOF detection. Today it has zero direct tests. Core has MixerCoreTests and MixPlannerTests (units), but nothing exercises AudioMixerEngine.RunSessionAsync or PlayoutService.RunEncoderSessionAsync.
    - Add harness tests that drive the mixer with synthetic PCM readers (you already have FfmpegPcmSampleReader behind an interface? if not, extract it) and assert: crossfade midpoint reporting, early-EOF handling, off-air abort mid-item, timed-interrupt late-window coalescing, queue starvation → silence.
    - Add one fake-ffmpeg integration test for PlayoutService (replace ffmpeg with an in-process pipe) to verify the 5s restart loop, the silence bridge, and the off-air switch latency.
    This is where a "station goes silent at 3am" bug will live. Cover it before anything else.

    DONE — implemented as:
    - Seams: IPcmSampleReaderFactory (+ FfmpegPcmSampleReaderFactory), IMixerEncoderSink (+ ProcessEncoderSink), IMixerUpdatePublisher. AudioMixerEngine.RunSessionAsync now takes IMixerEncoderSink + Stream instead of a concrete Process; the ActiveSource.Reader type widened to IPcmSampleReader; a DisposeIfDisposable extension avoids forcing IDisposable on test fakes.
    - Tests: tests/WhipRadio.Orchestrator.Tests/AudioMixerEngineTests.cs — 6 tests driving the real state machine with in-memory TonePcmReader + PacingStream + FakeEncoderSink: empty-queue→silence, single-item report+complete, off-air flip lets current item finish, two-song HardCut both reported, encoder-exited throws (no deadlock), early-EOF logs "shorter than duration" and continues.
    - Verification: 51/51 Orchestrator tests pass (45 existing + 6 new, no regressions). Build clean (0 errors).
    - Still TODO from this item: the PlayoutService.RunEncoderSessionAsync fake-ffmpeg test (the restart loop / silence bridge / off-air-abort-mid-item) — moved to item #6 as the natural follow-on, since it needs an IFfmpegLauncher seam rather than the mixer's IMixerEncoderSink.

    2. Real health checks, not just Aspire defaults
    Extensions.cs only registers a "self" liveness check and only maps /health in Development. For a product you want, tagged and environment-independent:
    - Icecast reachable (HTTP HEAD to /admin or the mount).
    - ffmpeg binary present and executable.
    - RadioDbContext can open a connection.
    - Ollama writer room reachable (lightweight /api/tags).
    - Playout queue depth (warn if > N or == 0 for > M minutes).
    - Encoder-alive: PlayoutService exposes a heartbeat timestamp; health check fails if it's stale.
    - ACE-Step / TTS / analysis sidecar HTTP reachability.
    Map /health and /alive outside Development too (behind auth if exposed). Aspire's WaitFor uses the ready check — right now Orchestrator only waits on Icecast, not on the studios it depends on for content.

    DONE — implemented as:
    - src/WhipRadio.Orchestrator/Services/StationHealthChecks.cs — five IHealthCheck implementations: IcecastHealthCheck (Unhealthy if mount host unreachable), FfmpegHealthCheck (cached 60s probe of the binary; Degraded if it times out), RadioDbHealthCheck (Unhealthy if CanConnect fails), OllamaHealthCheck (Degraded if writer room unreachable — content stalls but the mount keeps streaming), EncoderHeartbeatHealthCheck (Unhealthy if no pump beat for 30s — catches crash-loops/hung ffmpeg).
    - src/WhipRadio.Orchestrator/Services/EncoderHeartbeat.cs — lock-free last-beat timestamp; PlayoutService stamps it every encoder loop iteration.
    - Program.cs registers all five with "ready" tags (encoder also "live"). ServiceDefaults/Extensions.cs now maps /health and /alive in every environment (with an auth/network-policy note for public exposure).
    - Verification: build clean; 51/51 Orchestrator tests pass. Health endpoints take effect on next station restart.
    - Still TODO from this item: playout-queue-depth health check (warn if 0 for >M min or >N) — superseded by the whipradio.playout.queue_depth metric in #3, but a queue-depth *health check* with thresholds is still worth adding; sidecar /health probes (ACE-Step/TTS/analysis) — see #8.

    3. Add app-level metrics and an alerting surface
    OTel is wired but only with default ASP.NET/HTTP/runtime instrumentation. Add custom meters for the things an operator actually needs at 3am:
    - playout_queue_depth, encoder_restarts_total, generation_failures_total{kind=music|tts|news}, icecast_listener_count, mixer_clip_count, generation_latency_seconds{kind}, db_command_duration_seconds.
    - PlayoutService already logs encoder crashes; turn those into a counter so a flapping encoder is visible in a dashboard, not just logs.
    Ship an OTLP exporter config (or a built-in /metrics Prometheus endpoint) and a tiny Grafana/aspirate dashboard. The data is half there; the meters are missing.

    DONE — implemented as:
    - src/WhipRadio.Orchestrator/Services/StationMetrics.cs — IStationMetrics + StationMetrics + NullStationMetrics. Meter "WhipRadio" with: whipradio.encoder.restarts (counter), whipradio.generation.failures{kind} (counter), whipradio.generation.latency{kind} (histogram, s), whipradio.mixer.transitions{strategy} (counter), whipradio.mixer.clips (counter), whipradio.playout.queue_depth (observable gauge), whipradio.mixer.transitions_this_session (observable gauge), whipradio.icecast.listeners (observable gauge), whipradio.icecast.listener_peak (observable gauge).
    - src/WhipRadio.Orchestrator/Services/IcecastListenerProbe.cs — BackgroundService polling Icecast /status-json.xsl every 30s, caching listener/peak so the observable gauges read on scrape without HTTP-in-callback.
    - Increment/record sites: PlayoutService (encoder restart counter), AudioMixerEngine.FlushDueLogsAsync (transition + clip counters), MusicProductionService (latency on success, failure counter on studio-unavailable/cycle crash), AnnouncementProductionService (latency on success, failure counter), NewsPackageProductionService (latency on success, failure counter on both package-level catch blocks).
    - OTel wiring: ServiceDefaults/Extensions.cs .AddMeter("WhipRadio") + .AddPrometheusExporter() in WithMetrics; app.MapPrometheusScrapingEndpoint() in MapDefaultEndpoints → /metrics. Package OpenTelemetry.Exporter.Prometheus.AspNetCore 1.16.0-beta.1 added to ServiceDefaults.csproj. If OTEL_EXPORTER_OTLP_ENDPOINT is set, all metrics also flow through OTLP.
    - Verification: build clean; 51/51 Orchestrator tests pass. IStationMetrics/NullStationMetrics keeps the mixer test harness zero-ceremony.
    - Still TODO from this item: db_command_duration_seconds histogram (EF Core has no built-in OTel meter; needs an EF interceptor or a DiagnosticListener hook); the Grafana/aspirate dashboard JSON (the /metrics endpoint is enough to scrape, but a prebuilt dashboard lowers the bar for operators).

    4. Encoder/Icecast resilience beyond the 5s restart
    PlayoutService swallows all exceptions and restarts in 5s forever. That's good for survival but bad for a sustained Icecast outage — you'll hot-loop ffmpeg crashes. Add:
    - Exponential backoff (5s → 30s → 60s cap) with a "station offline" status surfaced to the UI.
    - A max-restart-rate circuit breaker: if N crashes in M minutes, park the station and raise an alert instead of silently looping.
    - The reverse risk you already partially handle: IsPlayoutEnabledAsync returns true on DB failure so a hiccup can't take the station down. Good. Mirror that philosophy for Icecast — a dead Icecast shouldn't burn CPU.

    DONE — implemented as:
    - src/WhipRadio.Orchestrator/Services/EncoderResiliencePolicy.cs — pure, clock-driven policy (no DI/ffmpeg/SignalR) so behaviour is unit-testable with a controlled clock. Two signals share one rolling crash window: (a) NextBackoff() grows exponentially with crashes-in-window, capped at maxBackoff (default sequence 5s→10s→20s→40s→60s); (b) RecordCrash() returns true when the window holds `threshold` crashes → caller parks the station. A session that ran longer than successResetsAfter (default 120s) before crashing clears the window, so an unrelated late crash starts backoff from the floor instead of inheriting a hot-loop streak.
    - src/WhipRadio.Orchestrator/Services/PlayoutService.cs — ExecuteAsync now owns an EncoderResiliencePolicy fed real DateTime.UtcNow. On crash: metrics.EncoderRestarted(), RecordCrash(); if tripped → LogCritical + ParkStationAsync (persist PlayoutEnabled=false, surface StationStatus.Offline, block on 15s poll until operator re-enables On Air, then policy.Reset()). Otherwise statusReporter.Set(StationStatus.Reconnecting, reason, now+backoff) so the UI lamp reflects the retry, then Task.Delay(backoff). No more infinite 5s hot-loop into a dead Icecast.
    - src/WhipRadio.Orchestrator/Services/StationStatusReporter.cs — StationStatus enum widened to { Online, Reconnecting, Offline } so the UI can distinguish "retrying" from "parked".
    - src/WhipRadio.Orchestrator/Configuration/RadioOptions.cs (StreamOptions) — five knobs: EncoderInitialBackoffSeconds (5), EncoderMaxBackoffSeconds (60), EncoderSuccessResetsAfterSeconds (120), EncoderCrashThreshold (5), EncoderCrashWindowMinutes (5). All overridable via Stream__* env/config.
    - tests/WhipRadio.Orchestrator.Tests/EncoderResiliencePolicyTests.cs — 6 tests: backoff growth+cap (5→10→20→40→60→60), breaker trips at threshold inside window, stale crashes pruned outside window so breaker doesn't trip, long healthy session clears streak before next crash, Reset() clears window, threshold=1 trips on first crash.
    - Docs: Phase-0-Tech-Decisions.md §"Encoder / Icecast resilience" and README.md config table document the knobs and the park behaviour.
    - Verification: build clean; EncoderResiliencePolicyTests pass. (Could not re-run the full Orchestrator suite this session — the running station, PID 40872, locks the Orchestrator output DLLs against a rebuild. The committed code is what the live station is running.)

    5. Guaranteed-content fallback so the queue never starves to silence
    ShowRunnerService refills the queue, but if every generation pipeline (music, TTS, news) is failing simultaneously, listeners get the silence bridge — which is correct for the mount but bad for the product. Add an emergency filler library: a small set of pre-analyzed evergreen tracks/jingles/station IDs that the ProgramDirector can pull with no external dependency, and a policy that injects them when queue depth stays below a threshold for > X minutes. Treat it like a UPS for audio.

    DONE — implemented as:
    - Emergency fallback reuses already generated local tracks instead of a separate bundled filler library. First startup can still warm up with silence until at least one valid generated song exists.
    - src/WhipRadio.Orchestrator/Services/EmergencyFallbackTrackService.cs selects a playable non-retired track with an existing file under Radio:DataRoot, avoids the just-finished/queued/recent-fallback tracks when alternatives exist, and never calls LLM/TTS/news/music generation/analysis.
    - PlayoutService and AudioMixerEngine call the fallback selector immediately at an empty queue boundary before writing silence, so legacy and mixer playout behave the same.
    - PlayoutItem carries fallback origin into PlaybackReporter; PlayLogEntry persists WasFallback via the FallbackPlayLogProvenance EF migration; /api/playlog exposes it and PlayLog.razor shows a small fallback icon only on fallback-delivered rows.
    - Verification: fallback selector tests cover no-library, fallback origin, avoiding the just-finished track, and avoiding recent fallback repeats.

    6. SQLite is fine for homelab, risky for "reliable product"
    radio.db + WAL is fine on one box, but for 24/7:
    - Document and automate backup (online backup via VACUUM INTO on a schedule; verify restore).
    - Add a DB-health metric (WAL size, checkpoint backlog, write latency).
    - Decide the line where you move to Postgres. EF Core makes this a provider swap; do it before you have a schema migration that's painful on SQLite. The migration history is already large and phase-tagged, so the longer you wait the harder the cutover.
    The concurrency story matters: PlayoutService, ShowRunnerService, and the SignalR publishers all hit the same SQLite file. Watch for writer contention under load.

    DONE (Postgres cutover) — implemented as:
    - Provider swap: WhipRadio.Infrastructure now references Npgsql.EntityFrameworkCore.PostgreSQL (10.0.2) instead of Microsoft.EntityFrameworkCore.Sqlite; AddRadioPersistence uses UseNpgsql and fails fast if ConnectionStrings:radio is missing (the file-path/data-dir helpers were removed). RadioDbContextFactory (design-time) uses a local Postgres connection, overridable via RADIO_DESIGN_CONNECTION. EF Core Relational pinned to 10.0.9 so the whole solution unifies on one EF version (Npgsql 10.0.2 otherwise pulls 10.0.4).
    - Hosting: AppHost.cs adds a persistent PostgreSQL container with a named data volume (whipradio-pgdata); AddDatabase("radio") injects ConnectionStrings__radio and the orchestrator WaitFor(radioDb). Program.cs dropped the old SQLite path fallback.
    - Timestamps: src/WhipRadio.Infrastructure/Persistence/NpgsqlConfiguration.cs sets the Npgsql EnableLegacyTimestampBehavior switch via a [ModuleInitializer], so DateTime maps to `timestamp without time zone` (SQLite-like, Kind=Unspecified round-trip) consistently for both the app and `dotnet ef` tooling. Reversible follow-up: normalize to UTC + timestamptz.
    - Migrations squashed: the 32 phase-tagged SQLite migrations were deleted and replaced by a single InitialPostgres baseline generated against Npgsql (identity-by-default int keys; `timestamp without time zone` columns; owned SelectionRules and string-enum columns reproduced).
    - Slugs: SlugGenerator.Normalize lowercases incoming slugs at the read boundary (Postgres `=` is case-sensitive); writes were already lowercase.
    - Data migration: tools/WhipRadio.DbMigrator (throwaway console, not in the solution/CI) copies every DbSet SQLite→Postgres with FK checks disabled for the session, preserves original keys (ValueGeneratedNever subclass), realigns identity sequences, and asserts row-count parity. Delete it after a verified cutover.
    - Tests: both test projects moved to Postgres via Testcontainers (tests/TestSupport/PostgresTestDatabase.cs starts one container per assembly, builds the schema once on a template DB via MigrateAsync — a migration smoke test — and clones an isolated database per fixture). The ~21 duplicated in-memory-SQLite fixtures were centralized onto a shared tests/TestSupport/DbFixture.cs (two suites keep local fixtures for their custom seed helpers).
    - The IFfmpegLauncher fake-ffmpeg harness folded into this item's TODO was already implemented earlier (IFfmpegLauncher/ProcessFfmpegLauncher seam + FakeFfmpegLauncher in PlayoutServiceTests), so that sub-item is closed.
    - Verification: full solution + both test projects build clean (0 errors). Runtime verification — the InitialPostgres `database update`, the DbMigrator data copy, the app booting against the Postgres container, and the Testcontainers test run — is pending a running Docker daemon (Docker Desktop was down during implementation).

    7. Sidecar lifecycle and version pinning
    ACE-Step is pinned (torch 2.8.0 / CUDA 12.8) per Phase-0 — good. Audit the TTS and analysis sidecars the same way: lock Python deps, tag Docker images by digest not just latest, and give each sidecar a /health endpoint the Orchestrator can poll. Right now studios are operator-managed and WhipRadio "discovers" them; for a product, discovery + health + automatic restart-on-unhealthy should be owned by the app, not start-studios.ps1.

    TODO.

    8. Secrets and deployment hardening
    AppHost still has hackme-dev/hackme-admin as parameter defaults. For a product:
    - Move Icecast passwords and any API keys (ElevenLabs, etc.) to user secrets / env vars with no dev fallback in committed code.
    - Produce a real deployment artifact (aspirate manifest or docker-compose) — not just start.ps1/start-studios.ps1 — so a fresh machine can bring the station up reproducibly.
    - docs/licenses/ discipline is already called out in AGENTS.md; make sure it's actually populated and CI-checked.

    PARTIAL — secrets hardening DONE, deployment artifact still TODO:
    - .env-based secret loading, cross-platform (Windows/Linux/macOS/WSL). A committed .env.example templates the required keys; .env is gitignored (already was). The Aspire AppHost loads .env at startup via src/WhipRadio.AppHost/DotEnv.cs (walks up to the repo root, parses KEY=value with optional quotes, never overrides an existing real env var so CI/prod wins). AppHost.cs reads ICECAST_SOURCE_PASSWORD / ICECAST_ADMIN_PASSWORD via a RequiredSecret() helper that throws a clear "copy .env.example to .env" message if missing — no baked-in dev fallback. ICECAST_RELAY_PASSWORD falls back to the source password. The Icecast container, Orchestrator, and Web app all receive the secrets as env vars.
    - Removed every committed hackme-* default: AppHost.cs AddParameter defaults + literal relay password; RadioOptions.cs IcecastOptions.SourcePassword/AdminPassword (now ""); src/WhipRadio.Orchestrator/appsettings.json (SourcePassword line removed); deploy/icecast/icecast.xml (:-hackme-* shell fallbacks stripped — envsubst now requires the vars). Orchestrator Program.cs adds a startup guard that throws if Icecast:SourcePassword is empty, so a misconfigured standalone run fails fast instead of hot-looping ffmpeg against a rejected push.
    - start.ps1 auto-copies .env.example to .env on first run with a yellow warning.
    - docs/licenses/ is already populated (5 files: models-and-services, containers-and-system-packages, python-sidecars, dotnet-nuget, README) — the third bullet is satisfied; a CI presence/CI-check gate is still worth adding.
    - Updated README.md (Secrets subsection + config table), AGENTS.md (Security & Configuration Tips).
    - Verification: AppHost.cs + DotEnv.cs compile clean (0 CS errors; build reached the copy step — only file-lock errors from the running station). Orchestrator Program.cs + RadioOptions.cs compile clean (0 CS errors; same file-lock caveat). grep confirms zero "hackme" in committed source/config (only this review file's historical description remains).
    - Still TODO from this item: the real deployment artifact (aspirate manifest or docker-compose) so a fresh machine brings the station up reproducibly without start.ps1/start-studios.ps1; a CI check that docs/licenses/ stays populated and that no secret defaults creep back into committed code.

    9. Turn the "blocked" status work into a full station-state machine
    Recent commits show you're refining status terminology (Blocked). Formalize station + host + production states into one enum-backed state machine with explicit transitions and invariants, and test those transitions. Right now status is spread across StatusBadge UI, DB flags (PlayoutEnabled, MixerEnabled), and per-service queues; a tested state machine prevents the class of bug where "off air" and "blocked" disagree across surfaces.

    TODO.

    10. E2E smoke test of the whole chain
    One test (can run nightly, not on every commit): bring up AppHost + a stub Icecast + stub studios, enqueue a known track, assert that bytes arrive at the Icecast mount within K seconds and that the now-playing SignalR event fires. This is the test that catches "the station compiles, tests pass, but nothing actually streams" — the failure mode you currently can't detect in CI.

    TODO.