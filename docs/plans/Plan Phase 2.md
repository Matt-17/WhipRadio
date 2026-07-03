# WhipRadio - Phase 2: Important Remaining Items

This file tracks only active Phase 2 follow-up work that is still worth doing.
Completed items, accepted deviations, stale implementation notes, and items now owned
by Phase 0 or later phase plans have been removed.

## 1. Startup Readiness And Progress

The station needs a clear startup readiness state instead of relying on logs and filler
talk while models and studios warm up.

Implement:
- A `ReadyToStream` or equivalent readiness signal for the minimum viable station:
  Ollama reachable, Icecast reachable, at least one usable voice booth, and enough audio
  or fallback behavior to avoid dead air.
- Progress/status surfaced in Admin or Console for model pulls, studio warmup, and
  unavailable dependencies.
- ShowRunner should wait for the minimum viable readiness gate, not for every optional
  remote studio.

## 2. Per-Use-Case Text Providers

Current routing has one `TextProvider` switch for all text generation. Keep that as the
default, but add use-case-specific overrides where they matter.

Implement provider selection for:
- Host/script copy.
- Lyrics and title generation.
- Program Director reasoning.
- Message moderation.

Each setting should fall back to the global provider when unset.

## 3. Empty Format Handling Without Genre Drift

When a format has no matching tracks, the selector falls back to any genre. That keeps
the stream alive, but it weakens the format identity.

Implement:
- Detect "no matching track for this format" before falling back.
- Queue a matching production request for the active format/subgenre.
- Air honest filler or another valid format while waiting; do not block the stream for a
  synchronous generation.
- Log and surface the reason so operators understand why the format drifted.

## 4. Configurable Talk Mix

Talk-kind probabilities are still hard-coded in `TalkPlanner`. The station should allow
operators to tune the balance of song intros, outros, banter, personal notes, jokes, and
station IDs without code changes.

Implement:
- A validated JSON or structured settings model for talk-kind weights.
- Sensible defaults matching current behavior.
- Unit tests for invalid weights and weighted selection.

## 5. Program Director Audit Trail

Director decisions currently live in logs only. Important planning changes should be
queryable after the fact.

Implement:
- `ProgramDirectorLog` or equivalent table.
- Store prompt summary, parsed actions, accepted/rejected changes, and failure reasons.
- Add a compact UI/API view for recent director decisions.
- Add an Orchestrator test project and cover director plan parsing.

## 6. Console Usability

The Console page still polls every 3 seconds and has no filters. It is serviceable, but
weak for debugging live incidents.

Implement:
- Level and category filters.
- Autoscroll toggle.
- SignalR push or another lower-latency update path.
- Preserve newest-first or oldest-first as an explicit UI choice.

## 7. Language Verification

Language control is prompt-level today. Add one safety pass for spoken text.

Implement:
- Detect likely wrong-language output for the station language.
- Regenerate once with a stricter prompt.
- If the second attempt still fails, log the issue and either use the best attempt or
  skip the talk based on severity.
