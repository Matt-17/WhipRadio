# WhipRadio Agent Tool Catalog

This document is the planning contract for tools available to WhipRadio's
Program Director, hosts/moderators, artists, and future guests.

The station uses a schema-constrained chat envelope with `reply` plus `actions`.
The names below are canonical English tool names. The chat text itself may be any
language, but tool names and parameters are stable English contracts.

Tools are executed only in the **chat** scope, through `ChatActionExecutor`. Every
other prompt scope (announcement writer, program-director day planner, message
moderation) parses a fixed structured-JSON schema and renders no tool list.

## Implementation Status (2026-07)

`Reply` and `NoOp` are not tool classes: an envelope with a `reply` and an empty
`actions` array is the reply/no-op. Shipped tool names sometimes differ from the
canonical names in this document; the mapping is noted below. The
`RequestBossApproval` confirmation flow is now built: destructive/authority-sensitive
verbs call `GateAsync`, which creates a `PendingApproval` row instead of acting; the
Boss approves or denies it from the chat approvals strip or the `/verbs` page, and
approval re-runs the verb through the same executor. Only the two production-internal
tools remain **Deferred**.

| Tool (this doc) | Status | Shipped as / note |
|---|---|---|
| `Message` | Shipped | — |
| `Announcement` | Shipped | hosts + news/weather specialists |
| `SearchMusic` | Shipped | hosts, specialists, director |
| `StatusReport` | Shipped | gained a `scope` argument; offered to hosts + director |
| `PlanFormat` | Shipped | director |
| `HireHost` | Shipped | director (general hosts) |
| `AssignHost` | Shipped | director |
| `LookupKnowledge` | Shipped | not previously in this doc; gated by `PodcastKnowledgeEnabled` |
| `InviteParticipant` | Shipped | as **`Invite`** |
| (group removal) | Shipped | as **`RemoveFromChannel`** |
| `SearchArtist` | Shipped | creates when missing via `ArtistCreationService` |
| `GetArtistProfile` | Shipped | deep background limited to director/self |
| `QueueTrack` | Shipped | hosts queue only during their own show |
| `PlanTalkBreak` | Shipped | `parts` is a semicolon `kind: purpose` list |
| `CreateTalkBit` | Shipped | — |
| `Remember` | Shipped | implicit self; hosts/specialists/artists (not director) |
| `ProduceNewsPackage` | Shipped | director + news specialist |
| `ProduceWeatherReport` | Shipped | director + weather specialist; no location override |
| `CreateSong` / `RequestSongFromArtist` (self) | Shipped | merged into **`MakeSong`** (artist self / director-named) |
| `RequestSongFromArtist` (relay) | Shipped | posts into a shared group channel and enqueues the artist |
| `PostArtistFeed` | Shipped | artist self; free-form body, no copywriter |
| `PlanConversation` | Shipped | as **`BriefPodcast`** |
| `CreateJingle` | Shipped | director |
| `SetJingleActive` | Shipped | director |
| `SetNewsPresenter` / `SetWeatherPresenter` | Shipped | director |
| `RetireTrack` | Shipped | director; reversible flag, no confirmation |
| `RequestBossApproval` | Shipped | full approval flow (`PendingApproval` + `ApprovalService`); gated verbs create one via `GateAsync` |
| `RetireArtist` | Shipped | director; reversible flag, no confirmation |
| `DeleteArtist`, `DeleteTrack`, `DeleteJingle` | Shipped | director + Boss approval |
| `RemoveShow`, `FireHost` | Shipped | director + Boss approval; `FireHost` shares `HostTermination.ApplyFireAsync` with the fire endpoint |
| `RedefineArtistProfile` | Shipped | director; approval when the artist has released tracks; uses `ArtistCreationService.RedefineArtistAsync` |
| `CancelSongProduction` | Shipped | artist self (free) or director (approval unless idle) |
| `CreateSpecialistHost` | Folded in | `HireHost` gained a `role` arg (`general`/`news`/`weather`); no separate verb |
| `EmergencyAnnouncement` | Shipped | director + on-air voices; emergency priority needs approval unless Boss-triggered; content aired via `AnnouncementFactory` (not strictly verbatim TTS) |
| `AnswerListenerMessage` | Shipped | on-air voices + director; queue/dismiss, publishes `ListenerMessagesChanged` |
| `ManageNewsFeed` | Shipped | director; add/update/delete need approval, toggle is direct |
| `SetNewsProductionSettings`, `SetWeatherSettings` | Shipped | director + approval (weather location change gated) |
| `SetStationSettings`, `SetProductionSwitch`, `SetProviderSettings` | Shipped | director; approval (playout-off / all settings); non-secret allow-list only |
| `StudioStatus`, `ServerStatus`, `PrivacyReport` | Shipped | read-only diagnostics |
| `MediaCleanupPreview`, `RunMediaCleanup` | Shipped | preview issues a token; run needs the token + Boss approval |
| `PlanGroupConversationTurns`, `RenderConversationSegment` | Deferred | production internals, not chat verbs |

The per-tool sections below are the design contract; where a tool is shipped under
a different name or with flattened string parameters, the shipped catalog
(`CharacterToolCatalog`) and `ChatActionExecutor` are the source of truth.

## Roles

| Role | Meaning |
|---|---|
| `Boss` | The human station owner/operator. This is the user, not an LLM agent. |
| `ProgramDirector` | The station programming authority. Can plan, hire, fire, assign, and manage schedules within guardrails. |
| `Host` | A normal radio host/moderator. Can talk, message, search music, discover artists, and request director help. |
| `NewsSpecialist` | A host with news content permissions. Has host permissions plus news package tools. |
| `WeatherSpecialist` | A host with weather content permissions. Has host permissions plus weather tools. |
| `Artist` | An artist or band member. Can reply, message, post to artist feed, and request songs for itself. |
| `Guest` | A future chat participant. Narrow communication-only role unless granted more. |
| `System` | Runtime notification source. May publish informational messages only. |

## Universal Rules

1. Every model action must be schema-constrained JSON. Free-form text is never
   parsed as an executable command.
2. `reply` is always allowed. It answers the participant or channel that sent
   the last message. The model must not choose another reply target.
3. If no side effect is needed, the agent returns a `reply` and an empty
   `actions` array.
4. Tools are offered by role and prompt scope. Unknown tools and unavailable
   tools are rejected before execution.
5. Every tool call is validated before execution: required parameters, value
   ranges, target existence, role permissions, and state preconditions.
6. Every side effect runs through the same Orchestrator service used by the UI
   or autonomous station. No tool may mutate EF entities directly from prompt
   text.
7. Agent-to-agent messaging is hop-capped and correlation-tracked. A
   host-to-host exchange must end with a terminal report to `Boss` or a
   bounded system stop.
8. Destructive, irreversible, or external-network-expanding actions require
   Program Director authority and, where marked, explicit `Boss` confirmation.
9. Destructive tools must resolve targets by stable id, or by one exact unique
   name match. Fuzzy matching is read-only.
10. Chat replies mirror the language of the last user message. On-air scripts,
    song lyrics, news, weather, and announcements use station/broadcast
    language rules.
11. Hosts cannot hire, fire, remove shows, delete artists, delete songs, edit
    station settings, change studios, or change provider/model settings.
12. Artists cannot modify schedules, hosts, stations, other artists, news,
    weather, studios, or library items outside their own artist identity.
13. System messages are informational only. Failed/rejected actions go to agent
    logs and concise system lines, never as fake consumer chat.

## Global Parameter Conventions

| Parameter | Type | Meaning |
|---|---|---|
| `reason` | string | Short operator-readable explanation for why the action is requested. |
| `priority` | enum | `low`, `normal`, `high`, `emergency`, or `scheduled`, depending on the tool. |
| `language` | string | BCP-47 tag such as `en` or `de`. On-air defaults to station language unless the tool explicitly concerns artist song language. |
| `targetId` | string | Stable id for the target entity when available. |
| `targetName` | string | Human name used only when exactly one active target matches. |
| `confirmationRequired` | boolean | Server-computed flag. The agent cannot set this to bypass confirmation. |

## Permission Matrix

`R` means read-only, `A` means allowed to act, `C` means allowed only after
explicit `Boss` confirmation, and `-` means unavailable.

| Area | ProgramDirector | Host | NewsSpecialist | WeatherSpecialist | Artist | Guest | System |
|---|---:|---:|---:|---:|---:|---:|---:|
| Reply and direct messages | A | A | A | A | A | A | R |
| Search status, music, artists | A | A | A | A | R | R | R |
| Make on-air announcements | A | A | A | A | - | - | - |
| Plan/remove shows and assign hosts | A/C | - | - | - | - | - | - |
| Hire/fire hosts | A/C | - | - | - | - | - | - |
| Discover/create artists | A | A | A | A | - | - | - |
| Artist feed and song creation | R | R | R | R | A | - | - |
| News feed settings | A/C | - | A/C | - | - | - | - |
| Weather settings | A/C | - | - | A/C | - | - | - |
| Library destructive actions | C | - | - | - | - | - | - |
| Studio/provider/settings changes | C | - | - | - | - | - | - |

## Communication Tools

### `Reply`

Goal: answer the sender of the last message or the current channel without any
additional side effect.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `text` | string | yes | Final prose reply. In the current envelope this is the top-level `reply` field, not an action item. |

Usage:

- Use for acknowledgements, explanations, refusals, status descriptions, or
  when a tool result already satisfies the request.
- Use with an empty `actions` array when no station action is needed.

Limitations:

- Cannot target another person or channel.
- Cannot imply an action succeeded before tool results confirm it.

Deterministic guardrails:

- Runtime always posts the reply to the active channel.
- Target is derived from the triggering message, never from model text.
- If the reply is empty and no actions are present, the turn is rejected as
  unhelpful unless the agent explicitly uses `NoOp`.

### `NoOp`

Goal: do nothing deliberately when action would be inappropriate.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `reason` | string | no | Short reason for staying quiet or taking no action. |

Usage:

- Use when a host has no useful off-air action, a request is impossible, or
  silence is better on-air.

Limitations:

- Does not send a message by itself. In chat, prefer `Reply` with no actions.

Deterministic guardrails:

- Always succeeds.
- No state mutation.

### `Message`

Goal: send a message to the Boss, Program Director, a host, an artist, a guest,
or an allowed channel.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `targetType` | enum | yes | `Boss`, `ProgramDirector`, `Host`, `Artist`, `Guest`, or `Channel`. |
| `targetId` | string | no | Stable id for host, artist, guest, or channel. |
| `targetName` | string | no | Exact unique display name if no id is known. |
| `message` | string | yes | Message body. |
| `purpose` | string | no | Why the message is being sent. |

Usage:

- A host can message the Program Director instead of promising a schedule
  change.
- A host can message another host to coordinate a segment.
- Any agent can message `Boss` with a status update, refusal, or result.
- Artists can message hosts or the Program Director about their own work.

Limitations:

- Does not directly execute the recipient's requested action. It only sends the
  message and may enqueue the recipient's turn.
- Cannot send messages as another participant.
- Cannot message inactive hosts unless reading an archived channel.

Deterministic guardrails:

- `targetId` wins over name. `targetName` must match exactly one active entity.
- Program Director target cannot self-forward to Program Director.
- Sending to a non-Boss agent increments hop count.
- If hop count exceeds `StationSettings.ChatMaxAgentHops`, the runtime posts a
  system stop instead of enqueueing another turn.
- Terminal `Message(targetType=Boss, ...)` ends a host-to-host exchange and
  prevents further queued agent messages from the same action set.

### `RequestBossApproval`

Goal: ask the Boss to confirm a pending destructive, costly, or
authority-sensitive action.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `actionTool` | string | yes | Tool name that needs approval. |
| `actionArgumentsJson` | string | yes | Serialized validated arguments for the pending action. |
| `summary` | string | yes | Human-readable summary shown to the Boss. |
| `risk` | enum | yes | `schedule`, `personnel`, `library`, `external`, `settings`, or `cost`. |
| `expiresInMinutes` | integer | no | Approval expiry, clamped by runtime. |

Usage:

- Use before firing a host, deleting media, removing a show, changing external
  feeds, or changing provider/model settings.

Limitations:

- Does not execute the action.
- Not available to hosts or artists for actions they cannot perform.

Deterministic guardrails:

- Runtime recomputes the pending action from validated arguments.
- The model cannot mark its own request approved.
- Expired approvals fail closed.
- Approval execution revalidates permissions and current state.

### `StatusReport`

Goal: summarize current station programming and operational state.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `scope` | enum | no | `station`, `schedule`, `production`, `studios`, `music`, `news`, `weather`, or `chat`. |
| `detail` | enum | no | `brief` or `full`. |

Usage:

- Program Director uses this before planning or explaining conflicts.
- Hosts may receive read-only status for their own show context if exposed.

Limitations:

- Read-only.
- Should not include secrets, API keys, or full hidden prompts.

Deterministic guardrails:

- Output is built from server state, not model guesses.
- Secret-bearing fields are always redacted.

### `Remember`

Goal: store a short memory note for continuity.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `subjectType` | enum | yes | `Host`, `Artist`, `ProgramDirector`, or `Station`. |
| `subjectId` | string | no | Stable id; implicit self when available. |
| `note` | string | yes | Short factual memory note. |
| `visibility` | enum | no | `private`, `director`, or `station`. |

Usage:

- Store that the Boss asked for a recurring segment style.
- Store that a host promised to follow up later.
- Store artist continuity after a release or interview.

Limitations:

- Not for secrets or credentials.
- Not for editing canonical profile facts such as names or voices.

Deterministic guardrails:

- Hosts and artists can write only self-scoped notes unless the Program
  Director writes the note.
- Notes are trimmed and length-limited.
- Duplicate recent notes are ignored.

## Music And Library Tools

### `SearchMusic`

Goal: search existing library tracks.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `query` | string | yes | Free-text search across artist, title, genre, subgenre, style, and story. |
| `limit` | integer | no | Maximum result count, clamped by runtime. |
| `includeRetired` | boolean | no | Include retired tracks. Program Director only. |

Usage:

- Find a track to discuss, play, compare, or recommend.
- In chat, use as an in-turn lookup whose results feed back into the next model
  round before final reply.

Limitations:

- Read-only.
- Does not enqueue playback.

Deterministic guardrails:

- Results come from `TrackQueryService`.
- Limit is clamped.
- Retired tracks are hidden except for Program Director diagnostics.

### `QueueTrack`

Goal: request a specific existing track for playout.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `trackId` | string | yes | Track id from search or library state. |
| `priority` | enum | no | `normal`, `high`, or `scheduled`. |
| `reason` | string | no | Why the track should play. |
| `dedicationMessageId` | string | no | Optional listener request message id. |

Usage:

- A host can request a fitting song for its own current show.
- Program Director can queue a track for programming purposes.

Limitations:

- Cannot play retired or missing tracks.
- Cannot force immediate interruption unless priority rules allow it.

Deterministic guardrails:

- Host use is allowed only for the current on-air host or for a non-immediate
  request routed to the Program Director.
- Runtime checks queue state, playout state, and track existence.
- Priority is clamped by role.

### `RetireTrack`

Goal: stop a track from future normal rotation without deleting its file or
history.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `trackId` | string | yes | Stable track id. |
| `reason` | string | yes | Why the track should leave rotation. |

Usage:

- Program Director removes a bad or off-brand track from future selection.

Limitations:

- Does not delete audio files.
- Does not remove play log history.

Deterministic guardrails:

- Program Director only.
- Exact id required.
- If currently queued or playing, active playout is not interrupted.

### `DeleteTrack`

Goal: delete a track row and audio file when safe.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `trackId` | string | yes | Stable track id. |
| `reason` | string | yes | Operator-readable deletion reason. |

Usage:

- Clean up failed/generated test tracks after review.

Limitations:

- Destructive.
- Cannot delete active playback immediately.
- Does not delete artist profile.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Exact id required.
- If active, deletion is queued until playback completes.
- If queued for playout but not active, tool fails with conflict.
- Play logs remain as deleted-track references.

### `SearchArtist`

Goal: find an artist matching a style, and optionally create a new artist when
none fits. This is the "search for a new artist" tool: for hosts and the Program
Director, discovery may create one.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `style` | string | yes | Desired artist sound or identity brief. |
| `genre` | string | no | Preferred genre. |
| `subgenre` | string | no | Preferred subgenre. |
| `language` | string | no | Preferred song language. |
| `vocalProfile` | string | no | Optional vocal direction such as instrumental, female lead, mixed vocals. |
| `createIfMissing` | boolean | no | Whether to create a new artist when no good match exists. Defaults true for Host and ProgramDirector. |
| `limit` | integer | no | Existing matches to return before creating. |
| `reason` | string | no | Why this artist is needed. |

Usage:

- A host wants "a smoky late-night trip-hop duo" for the next request.
- The Program Director wants to populate a new format lane.
- The tool first searches active artists; if no suitable artist exists and
  `createIfMissing` is true, it calls artist creation with the style brief.

Limitations:

- Artist creation may take time and may queue voice preparation.
- Does not create a song by itself.
- Artists cannot use this to create competitors or alter the roster.

Deterministic guardrails:

- Available to `ProgramDirector`, `Host`, `NewsSpecialist`, and
  `WeatherSpecialist`.
- Existing matches are returned before creation.
- Creation uses `ArtistCreationService`; the model never writes artist fields
  directly.
- New names/slugs are deduplicated server-side.
- If the style is empty, unsafe, or too broad, the tool fails and asks for a
  clearer brief.

### `GetArtistProfile`

Goal: read an artist profile, members, style, songs, and feed summary.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | no | Stable artist id. |
| `artistName` | string | no | Exact unique artist name. |
| `includeDeepBackground` | boolean | no | Include hidden generation background. Program Director only. |

Usage:

- Give a host context before an interview.
- Let an artist recall its own release history.

Limitations:

- Read-only.
- Hidden background is not exposed to hosts, guests, or other artists.

Deterministic guardrails:

- Artist targets resolve by exact id or exact unique name.
- `includeDeepBackground` is ignored unless caller is Program Director or the
  same artist.

### `RequestSongFromArtist`

Goal: ask an artist to create or propose a new song.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | yes | Target artist id. |
| `brief` | string | yes | Song request or creative direction. |
| `priority` | enum | no | `normal` or `high`. |
| `reason` | string | no | Why the station needs the song. |

Usage:

- Hosts or Program Director ask an artist for a song through chat.
- The artist then decides whether to call `CreateSong`.

Limitations:

- Does not queue production directly.
- The artist may refuse or ask a follow-up.

Deterministic guardrails:

- Available to Program Director and hosts.
- Posts to the artist's channel and enqueues the artist turn.
- If the artist is retired or inactive, the tool fails.

### `CreateSong`

Goal: let an artist request production of a new song for itself.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `brief` | string | yes | Creative brief for the song. |
| `mood` | string | no | Desired mood. |
| `targetDurationSeconds` | integer | no | Requested duration, clamped by station settings. |
| `vocals` | enum | no | `auto`, `instrumental`, or `vocal`. |
| `dedicationMessageId` | string | no | Optional listener request id to fulfill. |
| `postOnRelease` | boolean | no | Whether the artist should make an artist feed post after release. Defaults true. |

Usage:

- Artist answers a host or Boss request with "I'll make one."
- Artist creates a new track in its established style.

Limitations:

- Artist-only.
- Production can take minutes and depends on recording studio availability.
- Does not guarantee immediate playout.

Deterministic guardrails:

- The artist id is implicit from the chat actor; supplied ids are ignored or
  rejected.
- Runtime calls `MusicProductionControl.RequestTrackFor(selfArtistId)`.
- Duration is clamped to station settings.
- Vocal requests are gated by studio capability and artist member vocal
  capability.
- If a vocal reference is not ready, the request remains queued instead of
  generating the wrong kind of song.

### `CancelSongProduction`

Goal: cancel the current song production job when it belongs to the caller's
artist or when the Program Director is acting.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | no | Artist id. Required for Program Director diagnostics; implicit for Artist. |
| `reason` | string | yes | Cancellation reason. |

Usage:

- Artist realizes a request was wrong.
- Program Director stops a stuck generation.

Limitations:

- Only affects current in-flight production, not completed songs.

Deterministic guardrails:

- Artist can cancel only its own in-flight production.
- Program Director requires `Boss` confirmation unless the job is already
  failed/stuck by runtime status.
- Tool reports no-op when nothing is running.

### `PostArtistFeed`

Goal: create an artist feed post in the artist's own voice.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `body` | string | yes | Post body. |
| `kind` | enum | no | `status`, `song_teaser`, `release_note`, `behind_the_scenes`, or `reply`. |
| `trackId` | string | no | Optional own track id to link. |
| `visibility` | enum | no | `public` only for now. |

Usage:

- Artist posts about a new release, studio session, or upcoming appearance.

Limitations:

- Artist-only.
- Cannot post as the station, a host, or another artist.
- Not a chat reply; it appears in the artist feed.

Deterministic guardrails:

- Actor artist id is implicit and must match the post artist id.
- `trackId`, if present, must belong to the same artist.
- Body is sanitized, one-line normalized where needed, and length-limited.
- External links are stripped unless future policy allows them.

### `RedefineArtistProfile`

Goal: rebuild or repair an artist profile while preserving the artist identity.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | yes | Stable artist id. |
| `hint` | string | no | What should be repaired or emphasized. |
| `preserveName` | boolean | no | Runtime-enforced true. |

Usage:

- Program Director refreshes a weak generated profile.
- Artist asks the Program Director to improve its profile.

Limitations:

- Can change member roster, biographies, and voice prompts.
- May requeue member voice preparation.

Deterministic guardrails:

- Program Director plus `Boss` confirmation if the artist already has released
  tracks.
- Artist can request but cannot execute directly.
- Name and slug remain stable.
- Existing tracks are not rewritten.

### `RetireArtist`

Goal: stop an artist from future automatic production without deleting history.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | yes | Stable artist id. |
| `reason` | string | yes | Retirement reason. |

Usage:

- Program Director shelves an artist that no longer fits the station.

Limitations:

- Does not delete tracks or posts.
- Does not cancel currently playing tracks.

Deterministic guardrails:

- Program Director only.
- Exact id required.
- If the artist is in active production, retirement waits until production
  reaches a safe boundary or requires confirmation to cancel first.

### `DeleteArtist`

Goal: delete an artist profile only while it has no songs and no production
pending.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `artistId` | string | yes | Stable artist id. |
| `reason` | string | yes | Deletion reason. |

Usage:

- Remove a mistakenly created artist before any song exists.

Limitations:

- Destructive.
- Existing songs block deletion.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Exact id required.
- Runtime uses `ArtistDeletionService`.
- Fails if the artist has tracks or is queued/in production.

## On-Air And Production Tools

### `Announcement`

Goal: commission an on-air announcement in the current host voice.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `topic` | string | yes | Topic or brief for the announcement. |
| `priority` | enum | no | `normal`, `high`, or `emergency`. |
| `expiresInMinutes` | integer | no | Optional expiry for high/emergency announcements. |

Usage:

- Boss asks the on-air host to announce something.
- Host creates a short segment using the existing announcement production path.

Limitations:

- Production can take time.
- Not guaranteed to air instantly unless priority and queue state allow it.

Deterministic guardrails:

- Available only to `Host`, `NewsSpecialist`, and `WeatherSpecialist` in chat.
- Host must be the current on-air host. If another host is on air, the tool
  fails and the agent should message the Program Director or current host.
- Runtime uses `AnnouncementFactory` and `PriorityTalkBreakDispatcher`.
- On-air language follows station settings.

### `EmergencyAnnouncement`

Goal: create a direct emergency message and push it to the front of playout.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `text` | string | yes | Exact emergency message content. |
| `moderatorId` | string | no | Host voice to use. Defaults to current host. |
| `priority` | enum | no | `high` or `emergency`. |
| `expiresInMinutes` | integer | no | Expiry window, clamped by runtime. |

Usage:

- Program Director or current host handles urgent station messaging.

Limitations:

- This uses exact text, so it should be short and verified.
- Not for normal banter.

Deterministic guardrails:

- Program Director or current on-air host only.
- `emergency` priority requires `Boss` confirmation unless the Boss triggered
  the request directly in the same channel.
- Moderator target must be active.
- Runtime clamps expiry.

### `PlanTalkBreak`

Goal: create a scheduled talk break with one or more parts.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `title` | string | no | Operator-visible title. |
| `partsJson` | string | yes | Ordered list of parts with kind, purpose, speaker, and desired duration. |
| `priority` | enum | no | `normal`, `high`, or `scheduled`. |
| `targetTime` | string | no | Optional station-local planned time. |

Usage:

- Host plans a multi-part break for its own show.
- Program Director plans a station segment.

Limitations:

- Does not by itself generate all audio unless production workers pick it up.
- Hosts cannot assign other hosts without their agreement or director help.

Deterministic guardrails:

- Host can create only for self/current show.
- Program Director can plan for any active host.
- Part kinds are checked against host `AllowedTalkPartKinds`.
- Durations are clamped to available slot time and word budget.

### `PlanConversation`

Goal: plan a two-to-five participant conversation or podcast artifact.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `topic` | string | yes | Conversation topic. |
| `participantIds` | string | yes | Comma-separated host/artist/guest ids. |
| `targetDurationSeconds` | integer | no | Desired finished segment length. |
| `relatedTrackIds` | string | no | Optional comma-separated tracks to reference or schedule. |
| `priority` | enum | no | `scheduled`, `normal`, or `high`. |

Usage:

- Program Director schedules a podcast-style segment.
- A host-to-host coordination chain may end in a planned conversation record.

Limitations:

- True group conversation rendering is future Phase 5 work; until then this may
  create a planned `TalkBreak` artifact.

Deterministic guardrails:

- Program Director can schedule directly.
- Hosts can only propose or coordinate; terminal Admin/Director report creates
  the artifact under existing rules.
- Participant count is clamped.
- Every participant must be active and have a usable voice before rendering.
- Production budget limits max turns and duration.

### `CreateTalkBit`

Goal: create or request a reusable joke, anecdote, drop, or station bit.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `premise` | string | yes | Desired premise or theme. |
| `kind` | enum | no | `joke`, `anecdote`, `drop`, `station_bit`, or `personal_note`. |
| `hostId` | string | no | Target host. Implicit self for hosts. |

Usage:

- Host asks for reusable material around a recurring premise.
- Program Director seeds station bits.

Limitations:

- Does not guarantee immediate airplay.
- Can become repetitive if overused.

Deterministic guardrails:

- Host can create only for self.
- Program Director can create for active hosts.
- Runtime enforces repeat tolerance and retirement policy.

### `AnswerListenerMessage`

Goal: accept or prepare a listener greeting/request/dedication.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `messageId` | string | yes | Listener message id. |
| `action` | enum | yes | `queue_greeting`, `queue_dedication`, `dismiss`, or `reply_in_chat`. |
| `reason` | string | no | Dismissal or routing reason. |

Usage:

- Host handles a greeting or request during its show.
- Program Director triages pending listener items.

Limitations:

- Does not create a new song unless routed through artist/song tools.

Deterministic guardrails:

- Host can handle only messages assigned to self or unassigned current-show
  messages.
- Dismiss requires reason.
- Music request fulfillment must link to a track id or artist song request.

### `ProduceNewsPackage`

Goal: produce or recreate a news package.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `mode` | enum | yes | `next` or `recreate`. |
| `packageId` | string | no | Required for `recreate`. |
| `presenterId` | string | no | News specialist host id. |
| `reason` | string | no | Why production is requested. |

Usage:

- Program Director prepares the next top-of-hour package.
- News specialist recreates a failed package in its own role.

Limitations:

- Requires fresh news items.
- Depends on writer room and voice booth availability.

Deterministic guardrails:

- Program Director or `NewsSpecialist` only.
- Presenter must be active and news-specialist.
- Runtime uses existing news package production service.
- Failures become production notifications.

### `ProduceWeatherReport`

Goal: produce or request a weather segment for the configured station location.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `locationName` | string | no | Optional location override. Program Director only. |
| `latitude` | number | no | Optional latitude. Program Director only. |
| `longitude` | number | no | Optional longitude. Program Director only. |
| `presenterId` | string | no | Weather specialist host id. |
| `reason` | string | no | Why weather is needed now. |

Usage:

- Weather specialist prepares a weather handoff.
- Program Director requests an updated forecast.

Limitations:

- Weather source may be unavailable.
- Location override is settings-like and should not be casual.

Deterministic guardrails:

- Program Director or `WeatherSpecialist` only.
- Specialist can use only configured station location.
- Location coordinate changes require Program Director plus `Boss`
  confirmation.
- Presenter must be active and weather-specialist.

## Program Director Tools

### `PlanFormat`

Goal: create or update a format and write it into the weekly schedule.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `day` | string | yes | Day name in English/German, `today`, or `tomorrow`. |
| `startTime` | string | yes | Station-local `HH:mm`. |
| `durationMinutes` | integer | yes | Duration, clamped 30 to 240. |
| `genre` | string | yes | Primary genre. |
| `name` | string | no | Format name. |
| `description` | string | no | Format description. |
| `host` | string | no | Host id or exact active host name. |
| `reason` | string | no | Why the slot is planned. |

Usage:

- Program Director creates a new show or replaces overlapping slots.

Limitations:

- Overlapping slots are removed/replaced by design.
- Does not create a host unless paired with `HireHost`.

Deterministic guardrails:

- Program Director only.
- Time and duration are clamped.
- Host must be active.
- Schedule updates broadcast through existing SignalR path.
- If replacing more than one existing slot or changing the current on-air slot,
  require `Boss` confirmation.

### `RemoveShow`

Goal: remove a scheduled program slot or disable a format.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `slotId` | string | no | Program slot id to remove. |
| `formatId` | string | no | Format id to disable if removing a whole format. |
| `scope` | enum | yes | `slot_only`, `future_slots`, or `disable_format`. |
| `reason` | string | yes | Why the show is removed. |

Usage:

- Program Director cleans up or cancels scheduled programming.

Limitations:

- Does not delete historical play logs.
- Does not fire assigned hosts.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Exact id required for destructive scope.
- Cannot remove the current on-air item; changes affect future selection.
- If disabling a format leaves schedule gaps, runtime reports the gap.

### `AssignHost`

Goal: assign an active host to an existing format.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `format` | string | yes | Format id or exact name. |
| `host` | string | yes | Host id or exact active name. |
| `reason` | string | no | Why the assignment changes. |

Usage:

- Program Director moves a host into a show.

Limitations:

- Does not alter host persona or voice.

Deterministic guardrails:

- Program Director only.
- Format and host must resolve exactly.
- Specialist-only hosts should not be assigned to general shows unless the
  Director explicitly says so and the Boss confirms.

### `HireHost`

Goal: create a new general host from a short brief.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `brief` | string | yes | Short host hint. |
| `role` | enum | no | `general`, `news`, or `weather`. Defaults `general`. |
| `reason` | string | no | Why the station needs this host. |

Usage:

- Program Director creates a new host persona and voice.

Limitations:

- Writer room and voice booth can take time.
- Do not expose frontend-like name/gender/model pickers to normal chat.

Deterministic guardrails:

- Program Director only.
- Uses `SpecialistHostCreationService`.
- Station context is always included: station name, slogan, vision, mission,
  audience/format, and manual hint.
- Host voices must be designed `qv-` Qwen voices.
- Role `news` or `weather` may update matching station presenter setting.

### `FireHost`

Goal: deactivate a host and clean up active assignments.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `hostId` | string | yes | Stable host id. |
| `reason` | string | yes | Why the host is fired. |
| `replacementHostId` | string | no | Optional active replacement for affected formats. |

Usage:

- Program Director removes an unusable host from the roster.

Limitations:

- Historical play logs and announcements remain.
- Does not delete generated voice files.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Exact host id required.
- Runtime deactivates host, clears news/weather presenter settings, unassigns
  formats or assigns replacement, unassigns pending listener messages, retires
  active talk bits, expires pending/rendered talk breaks, and archives host chat
  channels.
- Cannot fire the last active general host unless replacement is created first
  or the Boss confirms the station may run fallback programming.

### `CreateSpecialistHost`

Goal: create a news or weather specialist when needed.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `role` | enum | yes | `news` or `weather`. |
| `hint` | string | no | Optional style/personality hint. |
| `reason` | string | no | Why specialist is needed. |

Usage:

- Program Director creates a presenter when no suitable news/weather host
  exists.

Limitations:

- Not for normal general hosts; use `HireHost`.

Deterministic guardrails:

- Program Director only.
- If no suitable specialist exists at runtime for required news/weather, create
  one rather than skipping the segment.
- No "automatic" or "first available" user-facing wording.

### `SetNewsPresenter`

Goal: assign the active news specialist used for news packages.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `hostId` | string | yes | Active news specialist id. |
| `reason` | string | no | Why the presenter changes. |

Usage:

- Program Director selects the news voice.

Limitations:

- Does not create the host.

Deterministic guardrails:

- Program Director only.
- Host must be active and `IsNewsSpecialist`.
- Broadcasts production update.

### `SetWeatherPresenter`

Goal: assign the active weather specialist used for weather segments.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `hostId` | string | yes | Active weather specialist id. |
| `reason` | string | no | Why the presenter changes. |

Usage:

- Program Director selects the weather voice.

Limitations:

- Does not create the host.

Deterministic guardrails:

- Program Director only.
- Host must be active and `IsWeatherSpecialist`.
- Broadcasts production update.

### `SetStationSettings`

Goal: change non-secret station behavior settings.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `settingsJson` | string | yes | JSON object containing only allowed settings. |
| `reason` | string | yes | Why settings should change. |

Usage:

- Program Director changes station name, slogan, language, queue length, or
  broadcast cadence after explicit operator direction.

Limitations:

- Must not set API keys or secrets.
- Provider/model changes are a separate tool.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Runtime allow-list decides editable fields.
- Values are clamped exactly like `/settings`.
- Language changes trigger host language alignment.
- Architecture decision docs must be updated when model defaults, studio
  ownership, images, voices, or audio behavior change.

### `SetProductionSwitch`

Goal: enable or disable station production/planning switches.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `switch` | enum | yes | `musicProduction`, `playout`, `news`, `weather`, or `greetings`. |
| `enabled` | boolean | yes | Desired state. |
| `reason` | string | yes | Why the switch changes. |

Usage:

- Program Director pauses music production during debugging.
- Program Director takes the station off-air only with confirmation.

Limitations:

- Does not start/stop the application or studios.

Deterministic guardrails:

- Program Director only.
- `playout=false` requires `Boss` confirmation.
- Agents never run `start.ps1`, `stop.ps1`, `start-studios.ps1`, or related
  scripts.
- Switch changes use existing settings/update services.

## Branding, News, Weather, And External Content Tools

### `CreateJingle`

Goal: generate a station jingle.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `label` | string | yes | Operator-visible label. |
| `style` | string | yes | Musical style prompt. |
| `durationSeconds` | integer | no | Target duration, clamped by runtime. |
| `reason` | string | no | Why the jingle is needed. |

Usage:

- Program Director refreshes station imaging.

Limitations:

- Depends on music generation backend.
- Does not automatically activate if policy says review first.

Deterministic guardrails:

- Program Director only.
- Duration clamped.
- Generated jingle uses station language and branding context.

### `SetJingleActive`

Goal: enable or disable an existing jingle.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `jingleId` | string | yes | Stable jingle id. |
| `isActive` | boolean | yes | Desired active state. |
| `reason` | string | no | Why the state changes. |

Usage:

- Program Director rotates station imaging.

Limitations:

- Does not delete the jingle.

Deterministic guardrails:

- Program Director only.
- Exact id required.

### `DeleteJingle`

Goal: delete an existing jingle.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `jingleId` | string | yes | Stable jingle id. |
| `reason` | string | yes | Deletion reason. |

Usage:

- Remove bad/generated test imaging.

Limitations:

- Destructive.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Exact id required.
- Cannot delete while queued or currently playing.

### `ManageNewsFeed`

Goal: add, update, enable, disable, or delete a news feed.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `operation` | enum | yes | `add`, `update`, `toggle`, or `delete`. |
| `feedId` | string | no | Required for update/toggle/delete. |
| `label` | string | no | Feed label. |
| `url` | string | no | RSS/feed URL. |
| `language` | string | no | Feed language. |
| `region` | string | no | Region key. |
| `category` | string | no | Category key. |
| `isEnabled` | boolean | no | Desired enabled state. |
| `pollCadenceMinutes` | integer | no | Poll cadence, clamped. |
| `maxItemsPerPoll` | integer | no | Max items, clamped. |
| `reason` | string | yes | Why the feed changes. |

Usage:

- Program Director or News Specialist maintains station news sources.

Limitations:

- External URL changes affect privacy and network behavior.

Deterministic guardrails:

- Program Director plus `Boss` confirmation for add/update/delete external
  URLs.
- News Specialist may propose through `RequestBossApproval`, not execute
  unconfirmed feed mutations.
- URL uniqueness and shape are validated server-side.
- Cadence and item limits are clamped.

### `SetNewsProductionSettings`

Goal: configure news package production.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `settingsJson` | string | yes | Allowed news production settings. |
| `reason` | string | yes | Why settings change. |

Usage:

- Program Director changes cadence, category order, max package duration, or
  presenter.

Limitations:

- Does not manage feeds; use `ManageNewsFeed`.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Runtime allow-list and clamps match `/production/news/settings`.
- Presenter must be active news specialist.

### `SetWeatherSettings`

Goal: configure weather production and location.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `settingsJson` | string | yes | Allowed weather settings. |
| `reason` | string | yes | Why settings change. |

Usage:

- Program Director changes weather cadence, full handover, location, or
  presenter.

Limitations:

- Location affects on-air facts and should be explicit.

Deterministic guardrails:

- Program Director plus `Boss` confirmation for location changes.
- Runtime clamps cadence and coordinates.
- Presenter must be active weather specialist.

## Operations And Diagnostics Tools

### `StudioStatus`

Goal: read studio runtime and pending operation status.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `kind` | enum | no | `writer`, `recording`, `voice`, `analysis`, or `all`. |

Usage:

- Explain why a generation or announcement is slow.

Limitations:

- Read-only.

Deterministic guardrails:

- Does not start, stop, restart, or rebuild studios.
- Secret fields are redacted.

### `PrivacyReport`

Goal: read recent external request and service privacy diagnostics.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `classification` | enum | no | `all`, `local`, `external`, or `cloud`. |

Usage:

- Program Director or Boss asks what services the station contacted.

Limitations:

- Read-only.

Deterministic guardrails:

- API keys and auth headers are never included.

### `ServerStatus`

Goal: read host resource, storage, GPU, and uptime diagnostics.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `detail` | enum | no | `brief` or `full`. |

Usage:

- Explain slow generation or storage pressure.

Limitations:

- Read-only.
- Not a process control surface.

Deterministic guardrails:

- Does not run shell commands.
- Data comes from existing diagnostics services.

### `MediaCleanupPreview`

Goal: preview unreferenced media cleanup.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `area` | enum | no | `tracks`, `announcements`, or `all`. |

Usage:

- Program Director checks whether cleanup would remove unreferenced files.

Limitations:

- Preview only.

Deterministic guardrails:

- Read-only.
- Uses server cleanup planner.

### `RunMediaCleanup`

Goal: delete unreferenced media files from the data root.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `area` | enum | no | `tracks`, `announcements`, or `all`. |
| `previewToken` | string | yes | Token from a recent cleanup preview. |
| `reason` | string | yes | Cleanup reason. |

Usage:

- Free disk space after reviewing preview.

Limitations:

- Destructive filesystem operation.

Deterministic guardrails:

- `Boss` confirmation required. Program Director can request but not execute
  alone.
- Requires a recent preview token generated from the same cleanup plan.
- Deletes only unreferenced files inside configured data root.

### `SetProviderSettings`

Goal: change model/provider defaults or API-backed generation settings.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `providerArea` | enum | yes | `text`, `music`, `tts`, `analysis`, or `studio`. |
| `settingsJson` | string | yes | Allowed non-secret provider settings. |
| `reason` | string | yes | Why settings change. |

Usage:

- Boss explicitly asks the Program Director to change defaults.

Limitations:

- Never stores secrets from agent text.
- Does not start or stop containers.

Deterministic guardrails:

- Program Director plus `Boss` confirmation.
- Secrets and API keys are excluded.
- AI/model default changes require updates to
  `docs/plans/Phase-0-Tech-Decisions.md` and, if work remains,
  `docs/plans/Phase-0-Deferred.md`.

## Future Guest And Group Conversation Tools

### `InviteParticipant`

Goal: invite a host, artist, member, or guest into a conversation channel.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `participantType` | enum | yes | `Host`, `Artist`, `ArtistMember`, or `Guest`. |
| `participantId` | string | yes | Stable participant id. |
| `channelId` | string | no | Existing channel id, or omitted to create one. |
| `reason` | string | no | Why the participant is invited. |

Usage:

- Program Director sets up a podcast planning channel.
- Host invites an artist into a DM-like collaboration.

Limitations:

- Group chat for artists/guests is future Phase 5 work.

Deterministic guardrails:

- Participant must be active.
- Host invitations to artists are allowed only for collaboration, not schedule
  changes.
- Program Director can invite any active participant.
- Group size is capped.

### `PlanGroupConversationTurns`

Goal: produce a bounded turn plan for a multi-person conversation.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `conversationId` | string | yes | Planned conversation id. |
| `maxTurns` | integer | no | Max turns, clamped by runtime. |
| `targetDurationSeconds` | integer | no | Desired duration, clamped by production budget. |
| `turnPolicy` | enum | no | `round_robin`, `addressed_to`, or `director_guided`. |

Usage:

- Phase 5 conversation director prepares a production plan.

Limitations:

- Planning only; does not render audio.

Deterministic guardrails:

- Program Director or conversation producer service only.
- Max participants, max turns, and duration are bounded.
- Every participant needs prompt context and a renderable voice.

### `RenderConversationSegment`

Goal: render a planned conversation into one premixed WAV.

Parameters:

| Name | Type | Required | Description |
|---|---|---:|---|
| `conversationId` | string | yes | Planned conversation id. |
| `renderMode` | enum | no | `sequential` first; future `bounded_overlap`. |
| `priority` | enum | no | `normal`, `high`, or `scheduled`. |

Usage:

- Phase 5 produces a podcast/talk item for playout.

Limitations:

- Expensive: multiple LLM and TTS calls.
- First implementation should use sequential turns.

Deterministic guardrails:

- Uses `SegmentRenderer` and `MixerCore`; live mixer is not used for offline
  conversation rendering.
- Production budget can downgrade or fail before spending unbounded resources.
- Output is one WAV queued as a normal playout item.

## Tools That Must Not Exist

These are intentionally not tools:

- `RunShellCommand`: agents must not run arbitrary commands.
- `StartApp`, `StopApp`, `RestartApp`, `StartStudios`, `StopStudios`,
  `RestartStudios`: the Boss operates station lifecycle scripts.
- `WriteSql`, `RunMigration`, `EditDatabase`: schema/data changes must go
  through reviewed application services and EF migrations.
- `SetApiKey`, `ReadSecret`, `ExposeSecret`: secrets are never agent-readable or
  agent-written.
- `DeleteShowAsHost`, `FireHostAsHost`, `HireHostAsHost`: hosts have no
  personnel or schedule authority.
- `PostAsAnotherArtist`, `CreateSongForAnotherArtist`: artists act only as
  themselves.

## Implementation Notes

- Add tools as `ICharacterTool` implementations and expose them only through
  `ICharacterToolCatalog.GetTools(scope, role)`.
- Keep chat tool output inside the existing `ChatReplySchema` envelope.
- Keep lookup tools (`SearchMusic`, `StatusReport`, future `SearchArtist`) as
  in-turn tools when the result should inform the final reply before any chat
  message is posted.
- Keep long-running tools detached from the chat turn and report results through
  action records, agent logs, and station notifications.
- Use existing services first:
  - `ChatService` for all chat writes.
  - `AnnouncementFactory` and `PriorityTalkBreakDispatcher` for announcements.
  - `DirectorPlanningService` and `SpecialistHostCreationService` for director
    actions.
  - `ArtistCreationService`, `ArtistSocialFeedService`, and
    `MusicProductionControl` for artist work.
  - `TrackQueryService` and `TrackDeletionService` for library work.
  - `INotificationBus` for proactive station messages.
- Add tests for every new tool:
  - role availability matrix;
  - schema validation and missing parameter failures;
  - exact target resolution;
  - destructive confirmation flow;
  - no side effects when denied;
  - hop-cap and terminal-report behavior for messages.
