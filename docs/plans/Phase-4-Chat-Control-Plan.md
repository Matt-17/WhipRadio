# WhipRadio — Phase 4 Implementation Plan: Chat Control

> **How to read this document.** This is the executable implementation plan for
> `Phase-4-Chat-Control.md`. It is written so an agent can implement the full feature
> without further support. Decision checkboxes `[ ]` mark places where multiple valid
> approaches exist — **the user ticks one per group before (or during) implementation**;
> the first option in each group is the recommended default and is safe to assume if a
> group is left unticked. Task checkboxes in the Master TODO are progress tracking.
>
> **Prime directives (from the brief, verified against the repo):**
> 1. Chat actions call the **same services** the autonomous station uses. No parallel code paths.
> 2. **Do not build a new action-protocol stack.** Phase 3b already shipped
>    `ICharacterTool` / `ICharacterToolCatalog` / `CharacterToolCallParser` /
>    `CharacterToolSchema` in `src/WhipRadio.Core/Prompting/` and
>    `src/WhipRadio.Infrastructure/Prompting/CharacterToolCatalog.cs`. Phase 4 extends them.
> 3. Malformed model output never crashes anything; the agent gets a system error message
>    and retries (bounded).
> 4. Agent-to-agent exchanges are hop-capped, must end in a terminal action, and are fully logged.
> 5. All UI text is English. Follow `DESIGN-GUIDE.md` exactly. Never restart the app yourself;
>    the user runs `start.ps1` / `stop.ps1`.

---

## 0. Master TODO list

Execution order. Each item references its detailed section. Build must stay green after
every milestone (`dotnet build WhipRadio.slnx`; use `--artifacts-path D:\tmp\whipradio-artifacts`
or `-p:OutDir=scratch` if the running app locks bins).

### Milestone A — Data + plumbing (US1)
- [x] A1. Add `ChatChannel`, `ChatMessage` entities + enums in Core (§3.1, §3.2)
- [x] A2. Register DbSets + model config in `RadioDbContext`; add `Phase4Chat` EF migration (§3.4)
- [x] A3. Seed/bootstrap channels in `DbInitializer` path or lazily in `ChatService` (§4.1-T2)
- [x] A4. `ChatService` (Orchestrator, scoped): channel list, paged history, post message, mark read (§4.1-T3)
- [x] A5. Chat DTOs in `src/WhipRadio.Core/Api/RadioApiDtos.cs` (§4.1-T4)
- [x] A6. REST endpoints in `RadioApiEndpoints` (§4.1-T5)
- [x] A7. Hub events `ChatMessageAdded`, `ChatChannelUpdated`, `ChatAgentThinking` via `IHubContext<RadioHub>` (§4.1-T6)
- [x] A8. `ChatLiveClient` in Web (copy `RadioLiveClient` template) (§4.1-T7)
- [x] A9. `Chat.razor` page skeleton: channel rail + message pane + composer, live receive, history paging (§4.1-T8, §6)
- [x] A10. NavMenu entry + `chat` icon (§4.1-T9, §10)
- [x] A11. Milestone check: admin messages persist, survive restart, appear live in a second browser tab

### Milestone B — Action protocol (US2)
- [x] B1. Add `PromptScope.Chat` to `PromptScope` enum (§4.2-T1)
- [x] B2. Chat reply envelope + `ChatReplySchema.Build(allowedTools)` (prose + 0..N actions) (§4.2-T2)
- [x] B3. `ChatReplyParser` built on `CharacterToolCallParser` semantics: tolerant, per-action validation, unknown-verb logging (§4.2-T3)
- [x] B4. Decide verb syntax + names (decision groups D1, D2 in §2.3)
- [x] B5. Validation-error → system message → bounded retry loop (max 2 retries) (§4.2-T4)
- [x] B6. Unit tests: malformed matrix, permission matrix (§9.1)

### Milestone C — Host verbs wired to services (US3)
- [x] C1. New chat tools: `Message`, `Announcement`, `SearchMusic` as `ICharacterTool`s with real availability rules (§4.3-T1)
- [x] C2. `TrackQueryService` (new) for `SearchMusic` (§4.3-T3)
- [x] C3. `ChatActionExecutor`: dispatch table verb → service; instant vs background actions; results persisted to `ChatMessage.ActionResultsJson` + surfaced in chat (§4.3-T4)
- [x] C4. `Announcement` verb → `AnnouncementFactory.ProduceAsync` + `PriorityTalkBreakDispatcher` fronting for high priority (§4.3-T2)

### Milestone D — Agent turn assembly (US4)
- [x] D1. Extend `PromptContextInput`/`PromptContext`/`PromptContextBuilder` with chat facts (channel kind, counterpart, chat history slice) (§4.4-T1)
- [x] D2. `ChatAgentTurnService`: build context → LLM call w/ envelope schema → parse → validate → dispatch → persist → broadcast (§4.4-T2)
- [x] D3. `ChatTurnQueue` (bounded channel) + `ChatAgentWorker` BackgroundService (§4.4-T3)
- [x] D4. Thinking indicator wiring (§4.4-T4)
- [x] D5. Integration test: fake LLM returns envelope → reply lands in DB + action executes (§9.2)

### Milestone E — Director agent (US5)
- [x] E1. Extract callable `DirectorPlanningService` from `ProgramDirectorService` privates (§4.5-T1)
- [x] E2. Director tools: `PlanFormat`, `HireHost`, `AssignHost`, `StatusReport` (§4.5-T2…T5)
- [x] E3. Director DM channel + director agent turn (uses `PromptScope.ProgramDirector` persona facts) (§4.5-T6)
- [x] E4. Destructive-action confirmation flow per decision D6 (§4.5-T7)
- [x] E5. (Cheap win) `ProgramDirectorLog` audit table per decision D7 (§4.5-T8)

### Milestone F — Host-to-host 1:1 loop (US6)
- [x] F1. `Message(to=host)` creates/uses A↔B channel, enqueues B's turn with `CorrelationId` + `HopCount+1` (§4.6-T1)
- [x] F2. Hop cap + terminal-action guard + exchange logging (§4.6-T2)
- [x] F3. Unprompted messages to Admin (B may `Message(Admin, …)`) (§4.6-T3)
- [x] F4. Coordination outcome per decision D5 (ConversationSegment fallback) (§4.6-T4)
- [x] F5. Loop test: two fake agents, assert cap + terminal enforcement (§9.1)

### Milestone G — Proactive messages (US7)
- [x] G1. `INotificationBus` in Core + `ChatNotificationBus` impl in Orchestrator (§4.7-T1)
- [x] G2. Publishers: announcement production failures, director plan results, show wrap-up, generation failures (§4.7-T2…T5)
- [x] G3. Firm rule enforcement: System messages carry no actions; only Director messages may (§4.7-T6)

### Milestone H — UI polish, hardening, cleanup (US8)
- [x] H1. Unread badges + last-message preview + channel sorting (§4.8-T1)
- [x] H2. Action chips with `StatusBadge` lifecycle (§4.8-T2)
- [x] H3. Composer UX: Enter/Shift+Enter, disabled-while-sending, autoscroll interop (§4.8-T3)
- [x] H4. Empty states, reconnect behavior, in-memory cap (§4.8-T4)
- [x] H5. `ChatCleanupService` retention job (§7.3)
- [x] H6. Full test pass + manual verification script (§9.3)
- [x] H7. Update `ARCHITECTURE.md` (new hosted services + chat flow) and `DESIGN-GUIDE.md` page inventory row

---

## 1. Architecture overview

```
Admin (browser)
  │  POST /api/chat/channels/{id}/messages          SignalR /hubs/radio
  ▼                                                     ▲
WhipRadio.Web  ──RadioApiClient──►  Orchestrator API    │ ChatMessageAdded / ChatAgentThinking
  ChatLiveClient ◄──────────────────────────────────────┘
                                        │
                                   ChatService (persist admin msg, bump channel)
                                        │ enqueue ChatTurnRequest
                                        ▼
                                   ChatTurnQueue ──► ChatAgentWorker (BackgroundService)
                                        │  per turn: create scope
                                        ▼
                                 ChatAgentTurnService
                                   1. PromptContextBuilder (Scope=Chat|ProgramDirector,
                                      persona + memory + on-air time math + chat history + tools)
                                   2. ITextGenerationService (envelope schema)
                                   3. ChatReplyParser (tolerant)
                                   4. validate per-role → on error: system msg + retry ≤2
                                   5. ChatActionExecutor → existing services
                                      (AnnouncementFactory, DirectorPlanningService,
                                       SpecialistHostCreationService, TrackQueryService, ChatService)
                                   6. persist reply + ActionsJson/ActionResultsJson
                                   7. IHubContext<RadioHub> broadcast
                                        │
        Message(to=host B) ─────────────┘ re-enqueue turn for B (CorrelationId, HopCount+1, cap)

Any Orchestrator service ──► INotificationBus.PublishAsync(...) ──► System message in station channel
```

Placement follows `ARCHITECTURE.md` boundaries: entities/contracts in Core, EF config in
Infrastructure, all runtime behavior in Orchestrator, Web stays a thin renderer.
Persistence uses `IDbContextFactory<RadioDbContext>` per operation (house pattern; no repositories).
LLM calls go through `ITextGenerationService` (the `TextGenerationRouter` already handles
Ollama/OpenAI routing, studio GPU leases, and has resilience handlers removed — chat adds
no new HTTP clients).

---

## 2. Decisions (user ticks one per group)

### D1 — Action wire format
- [x] **JSON envelope, schema-constrained (recommended).** The model returns one JSON object
      `{ "reply": "...", "actions": [ { "tool": "...", "arguments": { ... } } ] }` constrained via
      the Ollama/OpenAI schema channel, exactly like every other LLM output in the codebase
      (`StructuredJson` + `TextGenerationRequest.ResponseSchema`). Reuses `CharacterToolSchema`
      ideas; parsing reuses `CharacterToolCallParser` logic. Local models already do this reliably
      here (all of 3b/3c ships on it).
- [ ] Plain-text verbs (`Announcement(priority=high, topic="…")` mixed into prose), regex-tolerant
      parser. Matches the brief's original sketch; more "natural" transcripts but weaker guarantees
      and a second parsing dialect in the codebase.
- Either way, isolate behind **`IChatReplyParser`** (`Parse(raw, allowedTools) → ChatReply`) so the
  backend can swap to native tool calling for OpenAI later. The contract (`ChatReply { Prose,
  IReadOnlyList<CharacterToolCall> Actions, Errors }`) stays identical.

### D2 — Verb naming language
- [x] **English (recommended):** `Message`, `Announcement`, `SearchMusic`, `PlanFormat`, `HireHost`,
      `AssignHost`, `StatusReport`. Matches existing tools (`Announce`, `Play`, `Message`, …) and the
      English-UI rule. The *chat text* can still be any language; only verb identifiers are English.
- [ ] German verbs from the brief (`Nachricht`, `SucheMusik`, `PlaneFormat`, `StelleHostEin`,
      `WeiseHostZu`, `Statusbericht`) — register as case-insensitive aliases resolving to the same
      `ICharacterTool`s (parser already matches names `OrdinalIgnoreCase`; add an alias list to the
      definition or a lookup shim in the parser).
- [ ] Both: English canonical + German aliases accepted by the parser (cheap; one alias map).

### D2b — Permission model granularity
- [x] **Per role via `ICharacterTool.IsAvailable(PromptScope, CharacterRole)` (recommended).** This
      is the mechanism that already exists and already expresses "a host can't re-plan the week; the
      director can". Zero new schema; the §4.3-T1 availability table is implemented as `IsAvailable`
      overrides.
- [ ] Per-host capability flags (e.g. `Moderator.AllowedChatVerbs` CSV or a flags column) layered on
      top of the role check — enables "this host may not announce" per host. More schema + UI for a
      need that hasn't appeared; can be added later without breaking the role layer (the check just
      gains one more condition).

### D3 — Channel participant modeling
- [x] **Typed columns on `ChatChannel` (recommended for Phase 4):** `Kind` + `ModeratorId?` +
      `CounterpartModeratorId?` (only set for HostToHost). Simple queries, trivially seeded.
      Phase 5 migration to a member table is one EF migration later.
- [ ] `ChatChannelMember` table (`ChannelId, ParticipantKind, ParticipantId`) now — Phase 5-proof
      (artists/guests join channels), but more joins and bootstrap code for zero Phase 4 benefit.

### D4 — Reply language of chat agents
- [x] **Mirror the admin (recommended):** agents answer chat messages in the language the admin wrote
      (prompt instruction: "reply in the language of the last user message"). On-air output produced
      by actions always follows station broadcast language from `StationSettings` (existing rule:
      written/broadcast language = station settings; `Moderator.Language` is voice/accent only).
- [ ] Always station language in chat too (simpler, but German questions get English answers or
      vice versa).

### D5 — "Charlie + Jenny podcast" terminal outcome (Phase 3c.2 `ConversationSegment` is NOT implemented)
- [x] **Terminal = report + planned TalkBreak (recommended).** The exchange ends with Charlie
      messaging Admin the agreed topic, and the executor creating a `Scheduled` `TalkBreak`
      (Kind `Banter`, Purpose `"planned two-host podcast: <topic>"`) as the durable artifact.
      The DoD line "a ConversationSegment is created" is amended to "a planned segment record is
      created"; real `ConversationSegment` lands with 3c.2/Phase 5 and the executor swaps one call.
- [ ] Stub the `ConversationSegment` entity now (Phase 3c.2 §2 model shape, `Status=Planned`, no
      producer). Satisfies the DoD letter; adds a dormant table + migration surface.
- [ ] Pull a minimal 3c.2 slice into Phase 4 (plan + one-call script + per-turn TTS + concat WAV).
      Biggest scope increase; only pick this if podcasts are wanted *now*.

### D6 — Director destructive-action confirmation
- [ ] **Inline confirm chip (recommended).** Mutating director verbs (`PlaneFormat` overwriting
      existing slots, `HireHost`, `AssignHost`) are parsed and validated, then persisted as a
      *pending* action on the message; the chat renders Confirm/Dismiss buttons (amber chip). On
      Confirm, `POST /api/chat/actions/{messageId}/{actionIndex}/confirm` executes it. Read-only
      verbs (`StatusReport`, `SearchMusic`) and host verbs run immediately.
- [x] Everything auto-runs (trust the director; faster, riskier).
- [ ] Everything auto-runs except `HireHost` (hiring is the only irreversible-ish one; voice design
      costs GPU time).

### D7 — Director audit trail (Plan Phase 2 §5, nearly free here)
- [x] **Build it (recommended):** `ProgramDirectorLog` table (`Id, CreatedAtUtc, Source
      (Autonomous|Chat), PromptSummary, ActionsJson, Outcome, Error?`) written by
      `DirectorPlanningService` on every plan/act, chat-triggered or autonomous. Closes a known
      Phase 2 gap while the code is open.
- [ ] Skip; chat messages themselves are enough of a log for now.

### D8 — Unread tracking storage
- [x] **`ChatChannel.AdminLastReadAtUtc` column (recommended).** Single-admin console; server-side
      means unread counts survive browser changes. `POST /api/chat/channels/{id}/read` on open.
- [ ] Browser `localStorage` (no schema, but per-browser and invisible to future multi-device use).

### D9 — Hop cap + chat settings location
- [x] **`StationSettings` columns (recommended):** `ChatMaxAgentHops` (default 6),
      `ChatHistoryPromptMessages` (default 20), `ChatRetainedMessagesPerChannel` (default 500).
      Editable later on the Settings page (staged form → selects/number inputs, per DESIGN-GUIDE).
- [ ] `appsettings.json` section `Chat:` (no migration; not user-tunable at runtime).

### D10 — Announcement verb shape
- [x] **New distinct `Announcement(topic, priority)` chat tool (recommended).** In chat, the host
      *commissions* an announcement (topic → `AnnouncementFactory` writes the script in their voice);
      the existing `Announce(text)` tool stays as-is for `CharacterDecision` scope where the model
      already wrote final on-air text. Different semantics ⇒ different verb.
- [ ] Overload existing `Announce` with optional `topic`/`priority` args and scope-dependent
      behavior (fewer tools, muddier contract).

---

## 3. Data model

### 3.1 `ChatChannel` — `src/WhipRadio.Core/Entities/ChatChannel.cs` (new)

```csharp
public enum ChatChannelKind { Station = 0, HostDm = 1, DirectorDm = 2, HostToHost = 3 }

public class ChatChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatChannelKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;      // "Station", host name, "Program Director", "A ↔ B"
    public int? ModeratorId { get; set; }                  // HostDm / HostToHost: first host
    public int? CounterpartModeratorId { get; set; }       // HostToHost only: second host
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AdminLastReadAtUtc { get; set; }      // per D8
    public bool IsArchived { get; set; }                   // set when a host is fired
}
```
(If D3's member-table option is picked instead: drop the two moderator columns, add
`ChatChannelMember { Id, ChannelId, ParticipantKind (Admin|Host|Director|System|Artist|Guest), ParticipantId int? }`.)

### 3.2 `ChatMessage` — `src/WhipRadio.Core/Entities/ChatMessage.cs` (new)

```csharp
public enum ChatSenderKind { Admin = 0, Host = 1, Director = 2, System = 3 }
public enum ChatActionState { Parsed = 0, PendingConfirmation = 1, Running = 2, Succeeded = 3, Failed = 4, Dismissed = 5 }

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }
    public ChatChannel? Channel { get; set; }
    public ChatSenderKind SenderKind { get; set; }
    public int? SenderModeratorId { get; set; }            // Host senders only
    public string Text { get; set; } = string.Empty;       // the prose ("reply")
    public string? ActionsJson { get; set; }               // serialized List<ChatActionRecord>
    public Guid? CorrelationId { get; set; }               // ties an agent-to-agent exchange together
    public int HopCount { get; set; }                      // 0 for admin/system-originated
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// stored inside ActionsJson (System.Text.Json, camelCase like the rest of Core.Api):
public sealed record ChatActionRecord(
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    ChatActionState State,
    string? ResultSummary,   // "Announcement queued (High)", "3 tracks found: …", error text
    DateTime? CompletedAtUtc);
```
One JSON column for actions+results keeps the row model simple; the executor rewrites the
column as states change and re-broadcasts the message DTO so chips update live.

### 3.3 Indexes / config (in `RadioDbContext.OnModelCreating`)
- `ChatMessage`: index `(ChannelId, CreatedAtUtc)` (history paging), index `CorrelationId`.
- `ChatChannel`: unique filtered index on `(Kind, ModeratorId, CounterpartModeratorId)` to prevent
  duplicate DMs; store HostToHost pairs normalized (`ModeratorId < CounterpartModeratorId`).
- `Text` unbounded text; `ActionsJson` `jsonb`-mapped text (follow how `StrategyWeightsJson` etc. are mapped).
- Cascade delete messages with channel.

### 3.4 Migration

```powershell
dotnet ef migrations add Phase4Chat --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator
dotnet ef migrations list --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build
dotnet ef migrations has-pending-model-changes --project src/WhipRadio.Infrastructure --startup-project src/WhipRadio.Orchestrator --no-build
```
Include the D9 `StationSettings` columns and (per D7) `ProgramDirectorLog` in the same migration.
Migrations apply at startup via `DbInitializer.EnsureSeededAsync` — never from API handlers.

---

## 4. User stories & tasks

Conventions used below: "Core" = `src/WhipRadio.Core`, "Infra" = `src/WhipRadio.Infrastructure`,
"Orch" = `src/WhipRadio.Orchestrator`, "Web" = `src/WhipRadio.Web`.

### US1 — Chat foundation
*As the admin I see a Chat page with a station group chat and one DM per host (plus the
Program Director), can send messages, see them live, and history survives restarts.*

**T1. Entities + migration** — §3 above. Files: two new entity files in Core/Entities,
`RadioDbContext.cs` DbSets + config, migration.

**T2. Channel bootstrap** — `ChatService.EnsureChannelsAsync(ct)`:
station channel (create if missing), one `HostDm` per `Moderator where IsActive` (name = host
name), one `DirectorDm`. Called lazily at the top of every channel-list request (cheap idempotent
upsert) — **not** in `DbInitializer` — so hosts hired later get DMs automatically. When a host is
deactivated/fired, set `IsArchived = true` (archived channels render greyed at the rail bottom,
composer disabled).

**T3. `ChatService`** — Orch/Services, **scoped**, ctor-injects `IDbContextFactory<RadioDbContext>`
and `IHubContext<RadioHub>`:
```csharp
Task<IReadOnlyList<ChatChannelDto>> GetChannelsAsync(CancellationToken ct);              // + EnsureChannelsAsync + unread counts
Task<PagedChatMessagesDto> GetMessagesAsync(Guid channelId, DateTime? beforeUtc, int take, CancellationToken ct); // keyset, newest page first, AsNoTracking
Task<ChatMessageDto> PostAsync(Guid channelId, ChatSenderKind kind, int? moderatorId, string text,
                               string? actionsJson, Guid? correlationId, int hopCount, CancellationToken ct);
Task MarkReadAsync(Guid channelId, CancellationToken ct);
Task UpdateActionsAsync(Guid messageId, string actionsJson, CancellationToken ct);       // executor state updates
```
`PostAsync` persists, bumps `Channel.LastMessageAtUtc`, broadcasts `ChatMessageAdded` +
`ChatChannelUpdated`. Every other component posts chat messages **only** through this method.

**T4. DTOs** — append to Core/Api/RadioApiDtos.cs:
```csharp
public sealed record ChatChannelDto(Guid Id, string Kind, string Name, int? ModeratorId,
    string? PhotoUrl, DateTime LastMessageAtUtc, string? LastMessagePreview, int UnreadCount, bool IsArchived);
public sealed record ChatActionDto(string Tool, IReadOnlyDictionary<string,string> Arguments,
    string State, string? ResultSummary);
public sealed record ChatMessageDto(Guid Id, Guid ChannelId, string SenderKind, int? SenderModeratorId,
    string SenderName, string? SenderPhotoUrl, string Text, IReadOnlyList<ChatActionDto> Actions,
    DateTime CreatedAtUtc, Guid? CorrelationId, int HopCount);
public sealed record PagedChatMessagesDto(IReadOnlyList<ChatMessageDto> Messages, bool HasMore);
public sealed record PostChatMessageRequest(string Text);
```

**T5. REST endpoints** — in Orch/Api/RadioApiEndpoints.cs, group `/api/chat`:
```
GET  /api/chat/channels                                   → List<ChatChannelDto>
GET  /api/chat/channels/{id}/messages?before=&take=50     → PagedChatMessagesDto
POST /api/chat/channels/{id}/messages   {text}            → ChatMessageDto   (posts as Admin, then enqueues agent turn(s), §4.4)
POST /api/chat/channels/{id}/read                         → 204
POST /api/chat/actions/{messageId}/{actionIndex}/confirm  → 204              (D6 confirm chip)
POST /api/chat/actions/{messageId}/{actionIndex}/dismiss  → 204
```
Handlers stay thin (resolve scoped `ChatService`/queue, no workflow logic) per ARCHITECTURE.md.

**T6. Hub events** — extend `RadioHub`'s doc comment; broadcast to `Clients.All` (single-admin
console, matching every existing event):
- `ChatMessageAdded` → `ChatMessageDto`
- `ChatChannelUpdated` → `ChatChannelDto`
- `ChatAgentThinking` → `{ Guid ChannelId, string SenderName, bool IsThinking }` (anonymous DTO → add `ChatAgentThinkingDto`)

**T7. `ChatLiveClient`** — Web/Services, scoped, registered in Web `Program.cs`. Copy
`RadioLiveClient.cs` verbatim in structure: HTTP snapshot (`GetChannelsAsync` via `RadioApiClient`),
`HubConnectionBuilder` on `/hubs/radio`, `WithAutomaticReconnect`, `.On<…>` for the three events,
snapshot refresh on `Reconnected`, persistent 5s retry loop on `Closed`, 3s start timeout,
`event Action? Changed`. Add matching methods on `RadioApiClient` (`GetChatChannelsAsync`,
`GetChatMessagesAsync`, `PostChatMessageAsync`, `MarkChatReadAsync`, `ConfirmChatActionAsync`,
`DismissChatActionAsync`).

**T8. `Chat.razor`** — Web/Components/Pages, `@page "/chat"`, `@rendermode InteractiveServer`.
Full-height dashboard layout (like Console/Studio History — allowed per DESIGN-GUIDE for
continuous-scanning surfaces). See §6 for the full UI spec. Skeleton scope for this milestone:
rail lists channels, clicking loads latest 50 messages, composer posts, `Changed` re-renders,
new message in the open channel appends + autoscrolls.

**T9. Navigation** — `NavMenu.razor`: add `Chat` link (place next to Listener Messages) using the
new `chat` icon (§10).

**Edge cases:** empty text rejected (400); whitespace trimmed; messages capped at 4000 chars
(validate in endpoint); channel not found → 404; posting to archived channel → 409.

### US2 — Action protocol for chat
*A chat reply can mix prose with 0..N actions; parsing is tolerant; invalid actions produce a
retry loop, never a crash.*

**T1. `PromptScope.Chat`** — add to the enum in Core/Prompting/PromptContext.cs. Audit the few
`switch`es over `PromptScope` (PromptContextBuilder, tool `IsAvailable` overrides) and handle the
new value explicitly.

**T2. Envelope + schema** — Core/Prompting/ChatReplySchema.cs (new), mirroring
`CharacterToolSchema.Build` but wrapping:
```json
{ "type":"object",
  "properties":{
    "reply":{"type":"string"},
    "actions":{"type":"array","items":{
      "type":"object",
      "properties":{
        "tool":{"type":"string","enum":[ ...allowed tool names for this scope/role... ]},
        "arguments":{"type":"object","additionalProperties":{"type":"string"}}},
      "required":["tool","arguments"]}}},
  "required":["reply","actions"] }
```
Passed as `TextGenerationRequest.ResponseSchema` with `SchemaName = "chatReply"`. (If D1's
plain-text option is picked: no schema; instead a `PlainTextChatReplyParser` that regex-scans
`Verb(name=value, name="value")` lines case-insensitively, tolerant of quote styles/spacing,
strips them from the prose, ignores+logs unknown verbs.)

**T3. Parser** — Core/Prompting/ChatReply.cs + ChatReplyParser.cs (new):
```csharp
public sealed record ChatReply(string Prose, IReadOnlyList<CharacterToolCall> Actions,
                               IReadOnlyList<string> Errors);
public interface IChatReplyParser
{
    ChatReply Parse(string raw, IReadOnlyList<CharacterToolDefinition> allowedTools);
}
```
Implementation rules (mirror `CharacterToolCallParser` line by line where possible —
`StructuredJson.StripCodeFence` first, `OrdinalIgnoreCase` name match, required-arg check,
non-string JSON values `ToString()`ed):
- JSON parse failure → `Errors += "not valid JSON: …"` , `Prose = LlmOutputSanitizer.Sanitize(raw)`,
  `Actions = []` (the prose still gets shown; only actions are lost).
- Unknown verb → skip that action, log warning, add to `Errors` (drives the retry message).
- Missing required arg → same.
- **Never throw.** Property-based safety: any string input returns a `ChatReply`.
- Empty `reply` with valid actions is fine (action-only messages); both empty → treat as `NoOp`.

**T4. Retry loop** (lives in `ChatAgentTurnService`, §4.4): if `Errors` non-empty **or** an action
fails validation (permission/argument schema), append a *system correction message* to the LLM
conversation — `"Your previous output had errors: {errors}. Available tools: {rendered list}.
Respond again with the same JSON shape."` — and re-call, max 2 retries. After exhaustion: post the
last prose (sanitized) with the failed actions marked `Failed` + a System line in the channel
("2 action(s) could not be parsed"). Log every attempt (`ILogger`, category `Chat.AgentTurn`).

### US3 — Host verbs
*Telling an on-air host "make an announcement about X, high priority" produces it in their voice
and airs it respecting priority; hosts can search the library and message others.*

**T1. Register tools** — Infra/Prompting/CharacterToolCatalog.cs already discovers all
`ICharacterTool` DI registrations (see Orch `Program.cs` where AnnounceTool etc. are added —
register the new ones alongside). New classes in the same file or a sibling `ChatTools.cs`:

| Tool | Args | `IsAvailable` |
|---|---|---|
| `Message` (existing, gets a real handler) | `characterId` (host name, "Director" or "Admin"), `message` | any non-System role, scope `Chat` (keep existing availability for other scopes) |
| `Announcement` | `topic` (req), `priority` (opt: `normal\|high\|emergency`, default normal) | role `Host/NewsSpecialist/WeatherSpecialist`, scope `Chat` |
| `SearchMusic` | `query` (genre, mood, artist or free text), `limit` (opt, default 5, max 10) | role `Host/ProgramDirector`, scope `Chat` |
| `PlanFormat` | `day` (Mon..Sun), `startTime` ("HH:mm"), `durationMinutes`, `genre`, `name` (opt), `description` (opt), `host` (opt name) | role `ProgramDirector`, scope `Chat` |
| `HireHost` | `brief` | role `ProgramDirector`, scope `Chat` |
| `AssignHost` | `format` (name or id), `host` (name or id) | role `ProgramDirector`, scope `Chat` |
| `StatusReport` | — | role `ProgramDirector`, scope `Chat` |

Do **not** put execution logic in the tools' `ExecuteAsync` (they're Core-level contracts without
DB access); execution lives in `ChatActionExecutor` (T4) keyed by verb name. If D2 chose German
aliases: add `IReadOnlyList<string> Aliases` to `CharacterToolDefinition` (default empty) and
match aliases in both parsers.

**T2. `Announcement` execution** — resolve the sending host's `Moderator`; map priority
`normal→PromptPriority.Normal / high→High / emergency→Emergency`; then:
```csharp
var announcement = await announcementFactory.ProduceAsync(
    AnnouncementKind.Banter, moderator, relatedTrack: null,
    facts: $"The station admin asked for an announcement about: {topic}",
    stationName, ct, purpose: "chat-requested announcement");
```
Wrap in a `TalkBreak` exactly as `AnnouncementFactory` already does; for `high`/`emergency`
route through `PriorityTalkBreakDispatcher` so it fronts the queue (same path the emergency
system uses — verify the exact enqueue call in `PriorityTalkBreakDispatcher.PushReadyAsync`
and reuse it). This is a **background action** (production takes up to ~150 s): the executor
marks the action `Running`, returns immediately, runs production on a fire-and-forget task via
the existing `Forget()` helper (Core/Helpers), and on completion updates the action to
`Succeeded` ("Announcement queued for air, 27 s") or `Failed`, re-broadcasting the message. On
failure also publish to `INotificationBus` (US7).

**T3. `TrackQueryService`** — Orch/Services (new, scoped). `SearchMusic` is the one verb whose
result feeds **back into the same agent turn** (the brief: "returns results to the agent"):
```csharp
public sealed record TrackSearchResult(Guid Id, string Title, string ArtistName, string Genre,
    string? Subgenre, double DurationSeconds, int UpVotes, int DownVotes);
Task<IReadOnlyList<TrackSearchResult>> SearchAsync(string query, int limit, CancellationToken ct);
```
Implementation: `db.Tracks.AsNoTracking()` joined to Artists; match `query` tokens against
Genre/Subgenre (exact, case-insensitive) then Title/Artist `ILIKE %token%`; exclude retired;
order by vote score desc, then recency. **In-turn flow:** when the parsed actions contain
`SearchMusic`, the turn service executes it synchronously (fast, local DB), appends a system
message to the LLM conversation — `"SearchMusic results: 1. Artist – Title (Genre, 3:41) …"` —
and re-calls the model once so the agent can answer with actual knowledge ("I found three
synthwave tracks…"). This inner call does **not** count against the error-retry budget; allow at
most 2 chained `SearchMusic` rounds per turn.

**T4. `ChatActionExecutor`** — Orch/Services (new, scoped):
```csharp
public sealed record ChatActionContext(ChatChannel Channel, ChatMessage AgentMessage,
    Moderator? Sender, CharacterRole SenderRole, Guid CorrelationId, int HopCount);
Task<ChatActionRecord> ExecuteAsync(CharacterToolCall call, ChatActionContext ctx, CancellationToken ct);
```
Dispatch on `call.Name` (switch). Classification: **instant** (Message, SearchMusic, StatusReport,
AssignHost) execute inline; **background** (Announcement, PlanFormat, HireHost) flip to `Running`
and complete asynchronously as in T2. Every dispatch and result → `ILogger` info line
(`"chat action {Verb} by {Sender} in {Channel}: {Outcome}"`) so the Console page shows the audit
trail live. Validation before dispatch: re-check `catalog.GetTool(name, scope, role) != null`
(defense in depth — parser already filtered) and argument semantic checks (day parses, time parses,
duration 30–240 like `ProgramSlot`, host exists…). Semantic failure → action `Failed` with a
human-readable `ResultSummary`, which the retry loop (§4.2-T4) feeds back to the model.

### US4 — Agent turn assembly
*When the admin messages a host, that host's agent replies with full persona/memory/on-air
awareness and its permitted verb set.*

**T1. PromptContext extensions** — per the 3b firm rule (facts go in the builder, never ad hoc):
- `PromptContextInput`: add `Guid? ChatChannelId`, `string? ChatCounterpartName`,
  `ChatSenderKind? ChatAudience` (Admin or a host — so B knows it's talking to A, not to you).
- `PromptContext`: add `IReadOnlyList<string> ChatHistory` (rendered lines
  `"[12:04] Admin: bring me an announcement about the cow in the woods"`), `string? ChatAudience`.
- `PromptContextBuilder.BuildAsync`: when `Scope == Chat` and `ChatChannelId` set, load the last
  `StationSettings.ChatHistoryPromptMessages` (default 20) messages of that channel
  (`AsNoTracking`, chronological), render, and include; also extend `RenderSituation()` with a
  `Chat conversation:` block plus one line naming the audience. Everything else (persona, traits,
  memory slices, `RemainingSlotMinutes`, word budget, recent tracks) comes along for free — this
  is exactly why the brief says chat "depends hard on 3b".

**T2. `ChatAgentTurnService`** — Orch/Services (new, scoped). Signature:
```csharp
Task RunTurnAsync(ChatTurnRequest request, CancellationToken ct);
public sealed record ChatTurnRequest(Guid ChannelId, int? ResponderModeratorId /* null = Director */,
    Guid TriggerMessageId, Guid CorrelationId, int HopCount);
```
Steps:
1. Load channel + responder `Moderator` (or director mode); broadcast `ChatAgentThinking(true)`.
2. `promptContextBuilder.BuildAsync(new PromptContextInput { Scope = Chat (or ProgramDirector for
   the director — decide via responder), Moderator, Purpose = "chat conversation",
   ChatChannelId = … })`.
3. System prompt = existing persona rendering (`PersonaSummary`/`PersonaPrompt` conventions used by
   `AnnouncementWriter`) + `context.RenderSituation()` + rendered tool list (the catalog already
   renders definitions into prompts — reuse that renderer) + the envelope instruction with one
   worked example.
4. User prompt = the trigger message text (history is already in the situation block).
5. `llm.CompleteAsync(new TextGenerationRequest(system, user, "chat-turn",
   ChatReplySchema.Build(tools), "chatReply"), ct)`.
6. `IChatReplyParser.Parse` → validate → (§4.2-T4 retry / §4.3-T3 SearchMusic inner loop).
7. Execute remaining actions via `ChatActionExecutor`.
8. `ChatService.PostAsync(channel, Host/Director sender, prose, actionsJson, correlationId, hop)`.
9. `ChatAgentThinking(false)` in a `finally`.
Failure of the whole turn (LLM unreachable, timeout): post a System message "The host could not
answer (writer room unavailable)" — never leave the admin hanging on a spinner.

**T3. Queue + worker** — Orch/Services:
- `ChatTurnQueue` (singleton): `Channel.CreateBounded<ChatTurnRequest>(64)` with
  `BoundedChannelFullMode.DropOldest` + `TryEnqueue`, mirroring `HostVoiceQueue`.
- `ChatAgentWorker` (BackgroundService, registered `AddHostedService`): `await foreach` over the
  channel; per item `using var scope = scopeFactory.CreateScope();` resolve `ChatAgentTurnService`;
  try/catch everything (log + System failure message; the worker itself never dies). Turns are
  processed **serially** — one LLM/GPU conversation at a time is correct here, and the
  `TextGenerationRouter`'s studio leases already serialize GPU work anyway.
- Who gets a turn when the admin posts: `HostDm` → that host; `DirectorDm` → director; `Station`
  channel → **addressed-agent heuristic**: if the message starts with or contains a known host
  name / "Director" (case-insensitive word match), that agent responds; otherwise no one (System
  stays quiet; the station channel is primarily broadcast/notifications). Log when no responder
  matched. (Keep the heuristic in one small class, `ChatResponderResolver`, unit-testable.)

**T4. Thinking indicator** — `ChatAgentThinking` events from T2 render as a pulsing
`typing…`-style row (§6). Multiple queued turns simply show sequentially.

### US5 — Director agent
*Telling the director to plan a Friday-evening slot creates a real Format + schedule entry; the
director can hire and assign hosts and report status.*

**T1. Extract `DirectorPlanningService`** — Orch/Services (new, scoped). Move the *capability*
methods out of `ProgramDirectorService` (hosted loop keeps its cadence/trigger logic and calls the
new service; `DirectorControl` stays the manual-kick path):
```csharp
Task<DayPlanResult> PlanDayAsync(DayOfWeek day, string? brief, CancellationToken ct);       // wraps TryLlmDayPlanAsync + FallbackDayPlan + MaterializeDayAsync
Task<SlotPlanResult> PlanSlotAsync(DayOfWeek day, int startMinute, int durationMinutes,
    string genre, string? name, string? description, int? moderatorId, string? reason, CancellationToken ct); // create Format + upsert ProgramSlot(s), splitting/trimming overlaps
Task AssignHostAsync(Guid formatId, int moderatorId, CancellationToken ct);                 // Format.ModeratorId update + ScheduleChanged broadcast
Task<string> BuildStatusReportAsync(CancellationToken ct);                                  // see T5
```
`PlanSlotAsync` is new logic (the autonomous director plans whole days): validate duration 30–240,
`StartMinute` grid, resolve conflicts by trimming/removing overlapped slots **only after D6
confirmation**; reuse the materialization code path from `MaterializeDayAsync` for the
Format+slot creation so autonomous and chat-driven planning share one implementation (firm rule).
Broadcast the existing `ScheduleChanged` hub event after mutations so the Weekly Program page
updates live.

**T2. `PlanFormat` verb** → `PlanSlotAsync` (args mapped: day name → `DayOfWeek`, "HH:mm" →
startMinute; host name → moderator lookup). Result summary: "Friday 20:00–22:00 · 'Neon Nights'
(Synthwave) · host Alex".

**T3. `HireHost` verb** → `SpecialistHostCreationService.CreateAsync(role, hint, ct)`
(verified signature; `hint` is the free-text brief). **Required extension:** the
`SpecialistHostRole` enum currently only has `News` and `Weather`, and those branches set
`IsNewsSpecialist`/`IsWeatherSpecialist` and overwrite the `StationSettings` presenter ids. Add
`SpecialistHostRole.General` that skips both side effects (small, contained change in
`SpecialistHostCreationService` — the LLM plan/name/gender/voice pipeline is role-agnostic
already). Chat hiring calls `CreateAsync(SpecialistHostRole.General, brief, ct)`.
Voice design runs in the background via `HostVoicePreparationService` (existing) — the action
result says "Hired {name}; voice is being designed" and the DM channel for the new host appears on
the next channel-list fetch (T2 bootstrap). Qwen-only voice rule is already enforced by that
pipeline.

**T4. `AssignHost` verb** → `AssignHostAsync`; resolve format/host by name or id
(`SlugGenerator`-style loose matching not needed — exact case-insensitive name match, ambiguous →
`Failed` with candidates listed in `ResultSummary` so the model/user can disambiguate).

**T5. `StatusReport` verb** → `BuildStatusReportAsync`: compose from existing sources —
`ScheduleService.GetCurrentAsync()` (on air now, remaining minutes, next format), enabled format
count + names, active host count, planned-days coverage (max `ProgramSlot` day vs today), track
library size, pending `TalkBreak` count, last director plan result (from `ProgramDirectorLog` if
D7). Return as the *action result* and let the model weave it into prose? No — simpler and firm:
the executor runs `StatusReport` **in-turn like `SearchMusic`** (feed the facts back, one re-call)
so the director *speaks* the report in character. Cap the report at ~1200 chars.

**T6. Director identity** — the director is a service, not a `Moderator`. Turn assembly uses
`Scope = PromptScope.Chat` with `CharacterRole.ProgramDirector` for tool availability, and a fixed
director persona block (name "Program Director", station facts from context, a short standing
persona string stored as a constant or `StationSettings` later). `SenderKind.Director`,
`SenderModeratorId = null`, avatar = new `director` icon (§10) instead of `PersonAvatar`.

**T7. Confirmation flow (per D6)** — executor marks mutating verbs `PendingConfirmation` instead of
running; the chat message renders Confirm/Dismiss buttons; the confirm endpoint re-loads the
`ChatActionRecord`, executes via the same executor path, updates state, broadcasts. Dismissed →
`Dismissed` + System note. Pending actions expire after 24 h (flip to `Dismissed` in the cleanup
job, §7.3).

**T8. `ProgramDirectorLog` (per D7)** — entity + write in `DirectorPlanningService` (both
autonomous and chat callers land here automatically). Read API + a compact "Director Log" panel
can be a follow-up (§8), only the table + writes are in Phase 4 scope.

### US6 — Host-to-host 1:1 loop (Option B, real multi-agent)
*"Charlie, do a podcast with Jenny" → Charlie messages Jenny in their DM, Jenny (own context,
own memory) agrees and proposes a topic, Charlie reports back, a planned segment exists. Hop-capped,
terminal, fully logged.*

**T1. Message routing** — `ChatActionExecutor` handling of `Message(characterId, message)`:
- Resolve target: "Admin"/"you"/station-admin synonyms → post into the **current** channel (or the
  sender's HostDm if triggered from a HostToHost channel) as the terminal report; "Director" →
  DirectorDm + enqueue director turn; host name → find-or-create the normalized `HostToHost`
  channel for (sender, target).
- Post the message via `ChatService.PostAsync(…, correlationId: ctx.CorrelationId,
  hopCount: ctx.HopCount + 1)`.
- If target is a host or the director: enqueue `ChatTurnRequest` for the target with the incremented
  hop count, **unless the cap check (T2) fails**.
- The A↔B channel is fully visible to the admin in the rail (that *is* the logging surface),
  plus structured log lines per hop.

**T2. Guards** — before enqueueing an agent-to-agent turn:
- `HopCount + 1 > StationSettings.ChatMaxAgentHops` (default 6) → do not enqueue; post a System
  message into the exchange channel *and* the station channel: "Exchange between {A} and {B}
  reached the hop limit without concluding." Mark the correlation closed (in-memory set in
  `ChatTurnQueue` or just rely on hop math — hop math is sufficient and stateless; keep it
  stateless).
- **Terminal actions** end an exchange: `Message(Admin, …)`, `Announcement`, or (per D5) the
  planned-segment creation. When a turn's actions contain a terminal action, do not re-enqueue the
  counterpart even if the reply also contains a `Message` back (log it as closing courtesy —
  "freu mich drauf!" still posts, it just doesn't trigger another turn: enforce by checking
  `HopCount` of the *terminal* message).
- Prompt-side reinforcement: when `HopCount >= cap - 2`, the situation block appends "You have at
  most {n} exchanges left — conclude and report back to the admin now." (Cheap and very effective
  with local models.)

**T3. Unprompted admin messages** — `Message(Admin, …)` from inside an exchange posts into that
host's `HostDm` (that's where "Jenny: freu mich schon drauf!" lands unprompted). Unread badge +
`ChatChannelUpdated` make it visible.

**T4. Coordination outcome (per D5, recommended path)** — a new narrow tool available only in
`HostToHost` context… no: keep the verb set stable. Instead: the *initiating* host's terminal
`Message(Admin, …)` report is the required outcome; the planned segment artifact is created by the
executor when the report message matches a coordination correlation (i.e. if D5 option 1: the
initiator's final report triggers `TalkBreak` creation with `Priority=Scheduled`,
`Purpose = "planned two-host segment: {topic from the report}"`, both host ids in the title).
Simpler and firmer alternative if that feels too implicit — add director involvement exactly as the
brief sketches: Charlie's report goes to Admin *and* the Director, and the admin confirms a
director `PlanFormat`-like chip. Choose while implementing; both are ≤30 lines. Log either way.

### US7 — `INotificationBus` + proactive system messages
*The system reports real failures and events into the station group chat.*

**T1. Contract** — Core/Abstractions/INotificationBus.cs (new):
```csharp
public enum StationNotificationKind { Info = 0, Warning = 1, Failure = 2, WrapUp = 3, Director = 4 }
public sealed record StationNotification(StationNotificationKind Kind, string Text,
    string? Source = null, Guid? RelatedEntityId = null);
public interface INotificationBus { ValueTask PublishAsync(StationNotification notification, CancellationToken ct); }
```
**Impl** — Orch/Services/ChatNotificationBus.cs (singleton): writes through an unbounded
`System.Threading.Channels.Channel<StationNotification>` drained by a tiny hosted loop (pattern:
`InMemoryLogBuffer` + `ConsoleLogBroadcaster`), which creates a scope, posts a
`SenderKind.System` message into the station channel via `ChatService.PostAsync`, prefixed by kind
("⚠", "✕", "•" — use `&bull;`-style plain glyphs, no emoji, per console tone). Decoupling through
the channel keeps publishers non-blocking and safe to call from any worker.

**T2. Announcement/production failures** — in `AnnouncementProductionService` and the chat
announcement background path (US3-T2): on caught production exception →
`Publish(Failure, "Announcement for {host} could not be produced: {reason}", source: "production")`.
Include the "booth occupied 3×" case: where TTS/booth acquisition retries exhaust (locate the
retry site in the TTS/booth client or `StudioCoordinator` acquisition failure path), publish once
per failed job, not per retry.

**T3. Director plan results** — `DirectorPlanningService`: after autonomous weekly planning or a
chat-triggered `PlanDayAsync`, publish `Director`-kind summary ("Planned Friday: 6 slots, 2 new
formats."). Failures (`LLM day plan failed, fallback rotation applied`) publish as `Warning`.

**T4. Show wrap-ups** — `ShowRunnerService` already detects host change
(`_previousModeratorId != context.Moderator.Id` → `EnqueueHostChangeAsync`). Hook there: publish
`WrapUp` — "{host}'s shift ended ({format}). {n} tracks, {m} talk breaks." Counts from
`PlayLogEntry` within `ScheduleService.GetShowWindowsAsync().Previous*`. Keep it factual/cheap
(no LLM); a persona-voiced wrap-up is a §8 follow-up. Gate behind a bool
(`StationSettings.ChatWrapUpsEnabled`, default true, goes in the D9 settings batch).

**T5. Generation/model events** — wire the two or three highest-value existing failure logs
(music generation provider failure in `MusicProductionService`, mixer anomaly if a clear hook
exists in `AudioMixerEngine`/`TransitionLogEntry` writes) → `Warning`. Do not carpet-bomb: max ~5
publisher sites in Phase 4; more can follow.

**T6. Firm rule** — `ChatService.PostAsync` asserts: `SenderKind.System` ⇒ `actionsJson == null`.
Director-kind notifications that need a confirmable action are posted as `SenderKind.Director`
with a `PendingConfirmation` action (reuses the whole D6 chip machinery — e.g. "News package failed
twice — should I re-plan the hour?" with a confirmable `PlanFormat`).

### US8 — UI/UX polish + hardening
Detailed in §6/§7; tasks: **T1** unread badges (`UnreadCount` from `AdminLastReadAtUtc`), rail
sorted Station → Director → DMs by `LastMessageAtUtc` desc, archived last; last-message preview
(80 chars, single line, `text-overflow: ellipsis`). **T2** action chips (§6.3). **T3** composer
behavior (§6.4). **T4** `empty-state` blocks, `HasMore` "load older" button (ArtistFeed pattern),
in-memory cap 300 messages per open channel (Console pattern), reconnect → snapshot refresh
already handled by the LiveClient template.

---

## 5. Prompt design (system prompt skeleton for a chat turn)

```
{persona block — same rendering AnnouncementWriter uses for the host}

{context.RenderSituation()}   ← now includes the "Chat conversation:" history block + audience line

You are chatting off-air with {audience}. This is a private conversation, not broadcast.
Stay in character. Be concise like a real colleague on a phone chat.
Reply in the language of the last message addressed to you.   ← per D4

You can attach actions to your reply. Available actions:
{rendered tool list: name, description, arguments — from ICharacterToolCatalog for (Chat, role)}

Respond with JSON only:
{"reply":"<what you say in chat>","actions":[{"tool":"<name>","arguments":{"<arg>":"<value>"}}]}
Use an empty actions array when you only want to talk.
Example: {"reply":"Klar, mach ich sofort!","actions":[{"tool":"Announcement","arguments":{"topic":"the cow standing in the woods","priority":"high"}}]}
```
Notes: the worked example matters a lot for local models; keep it in. The tool list must be the
*filtered* per-role list (a host never sees `PlanFormat` — the brief's core permission idea, and
it's already how `CharacterToolCatalog.GetTools(scope, role)` works). Token budget: history slice
(20 msgs ≈ 600 tokens) + situation block fits gemma easily; trim history first if needed (§11).

---

## 6. UI/UX specification — `Chat.razor`

### 6.1 Layout (full-height dashboard, Console-style)

```
┌ stage ────────────────────────────────────────────────────────────────┐
│ CHAT                                    the back office line          │
│ ┌ rail 260px ──────────┐ ┌ conversation ───────────────────────────┐ │
│ │ ▸ Station        (3) │ │ header: [avatar] Alex — On air · Synthwave│ │
│ │ ▸ Program Director   │ │ ─────────────────────────────────────── │ │
│ │ ▸ Alex           (1) │ │  [avatar] Alex           14:02          │ │
│ │ ▸ Jenny              │ │  Sure — one high-priority announcement…  │ │
│ │ ▸ Charlie            │ │  [⚡ Announcement · high · Succeeded]     │ │
│ │ ▸ Charlie ↔ Jenny    │ │                         14:03  Admin     │ │
│ │   (archived, greyed) │ │            make it about the weather too │ │
│ │                      │ │  [· Alex is thinking …]                  │ │
│ │                      │ │ ─────────────────────────────────────── │ │
│ │                      │ │ [ textarea……………………………………… ] [send ▸]     │ │
│ └──────────────────────┘ └──────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────┘
```

- Root: new named layout class `chat-layout` (grid `260px 1fr`, `height: 100%` inside the
  full-height dashboard pattern used by Console/Studio History). Collapses to a single column
  under the existing small-screen media query (rail becomes a horizontal scroll strip or a
  select — match how Studio History collapses).
- One `h1.stage-title` "Chat" + `div.stage-sub` (radio-console voice, e.g. "the back office line").

### 6.2 Channel rail
- Rows: `PersonAvatar` `thumb` (hosts), `director` icon in an avatar-sized amber ring (director),
  `antenna` icon ring (Station); name in `--font-mono`; preview line in `--muted`; `TimeAgo` right;
  unread count as an amber pill (new small class `chat-unread`, mono, `min-width` for 1–2 digits).
- Active channel: amber left border + `--bg-2` fill (same active grammar as nav).
- HostToHost channels appear when they exist, labeled "A ↔ B", read-only composer with a muted
  note "agents only — you're reading their exchange".
- Archived: greyed, sorted last, composer disabled with note "host no longer at the station".

### 6.3 Message pane
- Reuse the ArtistFeed row anatomy but chat-adapted (new classes, don't overload artist-feed):
  `chat-msg` (grid: avatar 42px + bubble), `chat-msg.own` (admin: right-aligned, amber-tinted
  bubble `--bg-2` + amber border, no avatar), `chat-msg.system` (centered, mono, muted, smaller,
  no bubble — like a console line), `chat-msg.fresh` reuses the arrival animation keyframes.
- Bubble: `--bg-panel`, 1px `--line`, radius 6px, body text `--font-body`; meta line above
  (sender name mono + `TimeAgo`).
- **Action chips**: under the prose, one chip per `ChatActionDto`: `bolt` icon + verb + key arg
  (`Announcement · "the cow…" · high`) + `StatusBadge` mapping `Parsed/Running→amber(pulse)`,
  `Succeeded→green`, `Failed→red (title = ResultSummary)`, `Dismissed→muted`. `PendingConfirmation`
  chips additionally render `btn small primary` Confirm + `btn small` Dismiss (one size per row).
  Add the new states to `StatusBadge`'s tone map rather than hand-coding colors (design-guide rule).
- Thinking row: avatar + three-dot pulse (CSS animation, new `chat-thinking` class) driven by
  `ChatAgentThinking`.
- Day separators: centered mono muted line ("Tuesday, Jul 1") when the date changes between
  messages.
- "Load older" `btn small` at top when `HasMore` (prepend page, preserve scroll position:
  record `scrollHeight` before/after via the JS helper).

### 6.4 Composer
- `textarea` (2 rows, autogrow max 6 via JS helper) + `btn primary icon-only` send (`send` icon,
  `title="Send"`).
- Enter sends, Shift+Enter newline (`@onkeydown` handler checking `e.Key == "Enter" && !e.ShiftKey`;
  needs `@onkeydown:preventDefault` conditional — implement with the JS keydown helper if Blazor's
  conditional preventDefault gets awkward: one small function in the chat JS module).
- Disabled + working state while POST is in flight; failed send → inline muted error above the
  composer (`flash`-style but red only on real failure), text preserved.
- Focus the textarea on channel switch.

### 6.5 JS interop
New `wwwroot/chat.js` (keep `radio.js` for the player): `whipChat.scrollToBottom(el, smooth)`,
`whipChat.preserveScrollOnPrepend(el, fn?)` (or measure-before/after from .NET),
`whipChat.autogrow(el, maxRows)`. Loaded from `App.razor` next to radio.js. Autoscroll rule: only
auto-stick when the user is already within ~80px of the bottom (track via a scroll listener that
sets a data attribute), else show a small "↓ new messages" floating chip (amber, `btn small`).

### 6.6 Component reuse table

| Need | Use |
|---|---|
| Host avatar | `PersonAvatar` (`thumb` 42px in messages/rail) |
| Timestamps | `TimeAgo` (`AbsoluteAfter` ~12h) |
| Action state | `StatusBadge` (extend tone map) |
| Icons | `Icon.razor` (extended, §10) |
| Confirm hire/plan (if modal ever needed) | `ConfirmDialog` — but prefer inline chips (D6) |
| Empty channel | `empty-state` ("No messages yet — say hello to {name}.") |

---

## 7. Refactoring & hardening (required or strongly advised)

1. **`ProgramDirectorService` extraction (§4.5-T1)** — required for chat; also fixes testability
   (Orchestrator.Tests currently has 3 tests; the extraction makes director planning unit-testable
   with `DbFixture` + fake LLM).
2. **`SpecialistHostCreationService` general-role support (§4.5-T3 precheck)** — remove the hidden
   coupling where hiring always mutates news/weather presenter settings.
3. **`ChatCleanupService`** (hosted, daily, pattern `TalkBreakCleanupService`): trim each channel to
   `ChatRetainedMessagesPerChannel` (keep newest; never delete messages younger than 7 days),
   expire `PendingConfirmation` actions >24h, archive HostToHost channels idle >30 days.
4. **`ChatService.PostAsync` is the single write path** — enforce by making the `ChatMessages`
   DbSet writes internal to it; prevents future drift where a worker inserts rows without
   broadcasting.
5. **Serialization hygiene** — one `JsonSerializerOptions` (camelCase, ignore null) shared for
   `ActionsJson`, defined next to the record; never `JsonSerializer.Serialize` with defaults inline.
6. **No new HTTP clients** — chat rides on `ITextGenerationService`; the Aspire
   standard-resilience-handler trap (must be removed for long AI calls) therefore cannot re-occur.
   If any new client *is* added later, copy the registration pattern from
   `HttpClientsServiceCollectionExtensions.cs`.
7. **Station-channel noise control** — notification kinds render compactly; if the bus ever exceeds
   ~1 msg/min sustained, add per-kind coalescing (see §11) before adding more publishers.
8. **Fired hosts** — deactivating a host must archive its DM + any HostToHost channels
   (hook wherever `IsActive` flips — locate the hosts admin endpoint) so the rail never shows a
   live composer for a gone host.
9. **Docs** — ARCHITECTURE.md: add `ChatAgentWorker`, `ChatNotificationBus`, `ChatCleanupService`
   to the hosted-services list + a "Chat And Actions" flow section. DESIGN-GUIDE.md: add the Chat
   row to the page inventory ("Chat | Operator↔agent messaging | chat-layout rail + conversation
   pane, action chips").

---

## 8. Cheap adjacent features (after the core lands — each ≤ half a day)

- **Director Log panel** (needs D7): compact `console-table` on the Admin page or its own page —
  closes Plan Phase 2 §5 completely.
- **"Message host" shortcuts**: `transcript`-icon `btn small` on Hosts master-detail rows and on
  the Live Broadcast now-playing host chip → navigates to `/chat` with that DM opened
  (query param `?channel=`).
- **Announcement deep link**: a `Succeeded` Announcement chip links to the produced item in
  Play Log / lets you preview the WAV via the existing `/media/announcement/{id}` proxy route.
- **Wrap-ups in persona voice**: swap the factual wrap-up text for a 1-sentence LLM line built
  with the host's `PromptContext` (`Purpose = "shift wrap-up"`); the plumbing exists.
- **Browser notifications**: Web Notifications API for `Failure` notifications and DM messages
  when the tab is hidden (small addition to chat.js; permission prompt behind a Settings switch).
- **Chat → memory**: append a one-line DayMemory note after notable chat exchanges
  ("admin asked me for a high-priority announcement about X") via `ModeratorMemoryService.RememberAsync`
  — hosts then *remember* chat promises on air. (Recommend shipping this in Phase 4 already if
  time allows; it's ~10 lines in `ChatAgentTurnService` step 8.)
- **Guests/artists stub (brief §9 last question)**:
  - [ ] Strictly Phase 5 (recommended — D3's channel model migrates cleanly).
  - [ ] Stub now: add `Artist` to `ChatSenderKind` + nullable `SenderArtistId`, no UI.

---

## 9. Test directions

Baseline before starting: `dotnet test WhipRadio.slnx` — expect 240+ green; 2
`TopOfHourPackageDispatcherTests` failures are pre-existing on master (Orchestrator baseline
110/112). While the app runs, test with `--artifacts-path D:\tmp\whipradio-artifacts`.

### 9.1 Unit tests (Core.Tests / Orchestrator.Tests)
- **`ChatReplyParserTests`** (Core.Tests, mirror `CharacterToolCallParserTests` if present):
  valid envelope w/ 0, 1, 3 actions; prose-only; action-only (empty reply); fenced JSON;
  leading chatter before JSON; not-JSON-at-all (→ prose fallback, no throw); unknown verb
  (skipped + error); missing required arg; non-string arg values (numbers/bools stringified);
  duplicate verbs; 10 kB garbage input; German alias resolution (if D2). Assert **never throws**
  for any input.
- **`ChatToolAvailabilityTests`**: matrix over `(role × verb)` for `PromptScope.Chat` — host
  cannot see `PlanFormat/HireHost/AssignHost/StatusReport`; director cannot see `Announcement`;
  guest/artist see only `Message` (future-proofing); `System` sees nothing.
- **`ChatResponderResolverTests`**: station-channel addressing ("Alex, …", "hey ALEX", "Director:",
  no match, two names → first match wins).
- **Hop-guard tests**: pure functions over `HopCount`/cap/terminal-action detection (extract the
  guard into a static `ChatExchangeGuard` for exactly this reason).
- **`ChatNotificationBusTests`**: publish → drained → `PostAsync` called with System kind;
  System messages with actions rejected.

### 9.2 Integration tests (Orchestrator.Tests, `DbFixture` + fakes)
Reuse `SegmentTestFixtures` style: real DbContext (isolated Postgres per test), fake
`ITextGenerationService` returning canned envelopes, `FakeTtsEngine`, `StaticPromptContextBuilder`
where the real builder isn't the subject.
- **Agent turn happy path**: seed moderator + DM channel + admin message; canned LLM
  `{"reply":"Klar!","actions":[{"tool":"Announcement","arguments":{"topic":"cows","priority":"high"}}]}`;
  run `ChatAgentTurnService.RunTurnAsync`; assert reply row persisted with `ActionsJson`,
  announcement produced (fake TTS wrote a WAV), `TalkBreak` created with `Priority=High`.
- **Retry loop**: canned LLM returns garbage then a valid envelope; assert exactly 2 LLM calls,
  final message valid; then garbage×3 → assert System failure message + `Failed` actions.
- **SearchMusic in-turn**: seed tracks; canned sequence (action call → final prose); assert the
  second LLM call's prompt contains the rendered results.
- **Host-to-host loop**: canned LLMs for A and B where B always replies `Message(A, …)`; assert the
  exchange stops at the cap with the System notice; second variant where B's 2nd turn is
  `Message(Admin, …)` → assert terminal, planned `TalkBreak` exists (per D5).
- **Director PlanFormat**: canned envelope → `PlanSlotAsync`; assert `Format` + `ProgramSlot`
  rows (day/start/duration), `ProgramDirectorLog` row (if D7); overlap case → pending confirmation
  (if D6) or trimmed slots.
- **HireHost**: assert new `Moderator` with persona fields, no news/weather settings mutated,
  a `HostDm` channel appears on next `GetChannelsAsync`.
- **ChatService paging**: 120 messages → 3 keyset pages, stable order, `HasMore` correct.
- **Cleanup**: seed 600 messages → trimmed to retention floor, recent-7-days preserved.

### 9.3 Manual verification script (user starts the app via `start.ps1`; never auto-restart)
1. Open `/chat` — rail shows Station, Program Director, one DM per active host; badges zero.
2. Second browser tab: send from tab 1, appears live in tab 2 (SignalR path).
3. DM an on-air host "make a high-priority announcement about the cow in the woods" → thinking
   indicator → in-character reply + Announcement chip `Running→Succeeded` → item fronts the queue
   on Live Broadcast and airs in that host's voice (brief DoD 2).
4. Ask the same host "do you have time for a 5-minute story right now?" late in their slot →
   answer references remaining time (3b time math visible in chat).
5. DM the director "plan next Friday 20:00–22:00, synthwave night with Alex" → action chip
   auto-runs → Weekly Program page shows the new slot live (`ScheduleChanged`) (DoD 3).
6. Director: "hire a laid-back late-night host, female, dry humor" → action chip auto-runs → Hosts page shows
   the new host (voice designing), a new DM appears (DoD hiring).
7. "Charlie, plan a podcast with Jenny about synthwave" → watch the Charlie↔Jenny channel fill,
   Jenny's unprompted DM arrives, exchange terminates ≤6 hops, planned segment artifact exists
   (DoD 5/6).
8. Malformed-action robustness: temporarily set an absurd topic/limit or use a tiny model — confirm
   System error line instead of a crash (DoD 4).
9. Stop the TTS sidecar (`stop-studios.ps1` booth only) and request an announcement → a Failure
   notification appears in the station channel (DoD 7). Restart studios after.
10. Restart the app (user does it) → history intact, unread state intact.
11. Console page: every action dispatch/result visible as log lines.

---

## 10. Icon asset list (extend `src/WhipRadio.Web/Components/Icon.razor`)

House style: `viewBox="0 0 16 16"`, stroke = `currentColor` (inherited via `.ico` CSS), no fill
unless noted, keep 1.5-ish visual stroke weight consistent with existing paths. Add these cases:

| Name | Used for | SVG body |
|---|---|---|
| `chat` | Nav entry, chat page title | `<path d="M2 3.5a1.5 1.5 0 0 1 1.5-1.5h9A1.5 1.5 0 0 1 14 3.5v6a1.5 1.5 0 0 1-1.5 1.5H8l-3 3v-3H3.5A1.5 1.5 0 0 1 2 9.5Z"/>` (transcript bubble without the text lines) |
| `send` | Composer send button | `<path d="M2.2 8 13.8 2.6 11 13.4 7.6 9.4Z"/><path d="M13.8 2.6 7.6 9.4"/>` |
| `people` | Station group channel marker | `<circle cx="5.6" cy="5.4" r="2.2"/><path d="M1.8 13.2a3.8 3.8 0 0 1 7.6 0"/><circle cx="11.2" cy="5.9" r="1.7"/><path d="M10.9 9.6a3.2 3.2 0 0 1 3.3 3.6"/>` |
| `director` | Program Director avatar/marker | `<path d="M5.6 2.6h4.8v2H5.6Z"/><path d="M10.4 3.4h1.8a1 1 0 0 1 1 1v8.8a1 1 0 0 1-1 1H3.8a1 1 0 0 1-1-1V4.4a1 1 0 0 1 1-1h1.8"/><path d="M5.4 7.4l1.5 1.5 3.4-3.4"/><path d="M5.4 11h5.2"/>` (clipboard + check) |
| `bolt` | Action chips | `<path d="M8.8 1.8 3.6 9h3.2l-.8 5.2L11.2 7H8Z"/>` |
| `check-circle` | Succeeded action | `<circle cx="8" cy="8" r="6.2"/><path d="M5.2 8.2l1.9 1.9 3.7-3.9"/>` |
| `x-circle` | Failed/dismissed action | `<circle cx="8" cy="8" r="6.2"/><path d="M5.8 5.8l4.4 4.4M10.2 5.8l-4.4 4.4"/>` |
| `dots` | Thinking indicator (each dot pulses via CSS) | `<circle cx="3.4" cy="8" r="1.3" fill="currentColor" stroke="none"/><circle cx="8" cy="8" r="1.3" fill="currentColor" stroke="none"/><circle cx="12.6" cy="8" r="1.3" fill="currentColor" stroke="none"/>` |
| `bell` | Notification/system messages | `<path d="M8 2a3.8 3.8 0 0 1 3.8 3.8c0 3 .8 4.2 1.6 4.9H2.6c.8-.7 1.6-1.9 1.6-4.9A3.8 3.8 0 0 1 8 2Z"/><path d="M6.6 12.9a1.5 1.5 0 0 0 2.8 0"/>` |
| `exchange` | Host↔host channel marker / hop badge | `<path d="M3 5.6h8.4M9.4 3.4l2.2 2.2-2.2 2.2"/><path d="M13 10.4H4.6M6.6 8.2 4.4 10.4l2.2 2.2"/>` |

Existing icons reused: `transcript` (message shortcuts), `antenna` (station channel alt),
`close`, `refresh`, `play` (announcement preview follow-up). No new files under `wwwroot/` —
everything stays in `Icon.razor` per the design guide.

---

## 11. Optimization opportunities (in/near this feature)

- **Prompt token discipline**: cap rendered chat history at ~700 tokens (drop oldest lines first,
  keep the trigger message verbatim); `ChatHistoryPromptMessages` is the coarse knob.
- **Keyset pagination everywhere**: `WHERE ChannelId = @c AND CreatedAtUtc < @before ORDER BY
  CreatedAtUtc DESC LIMIT @take` (`AsNoTracking`) — never OFFSET; the `(ChannelId, CreatedAtUtc)`
  index makes history O(page).
- **Turn coalescing**: if multiple admin messages land in one DM before the agent answers, the
  worker can drain queued requests for the same channel and answer once against the latest history
  (dedupe by `ChannelId` when dequeuing) — better UX and fewer GPU calls.
- **Notification coalescing**: identical `(Kind, Source)` within 60 s → suffix "(×3)" instead of
  three rows (only add when noise appears; see §7.7).
- **DTO mapping in one place**: a `ChatMappings` static (entity→DTO) shared by service + endpoints
  avoids the drift that hit some earlier DTOs.
- **Free rider**: the `ChatAgentThinking` event pattern (transient per-channel busy state) is
  reusable for "director is planning…" on the Weekly Program page later.
- **Broader project wins unlocked**: director audit trail (D7) closes Plan Phase 2 §5; the
  `INotificationBus` gives Plan Phase 2 §1 (startup readiness) and §3 (empty-format fallback
  logging) an obvious operator-visible surface — both become "publish one notification" changes.

---

## 12. Definition of Done (brief §8, made concrete)

- [x] Group chat + per-host DMs + director DM live via `ChatMessageAdded` on `/hubs/radio`,
      history persisted in Postgres and paged (US1).
- [x] DM to an on-air host "make an announcement about X, high priority" → in-character chat reply
      + `AnnouncementFactory`-produced WAV in *their* Qwen voice, fronted by
      `PriorityTalkBreakDispatcher`, aired (US3; manual step 3).
- [x] Director chat "plan Friday 20–22 synthwave with Alex" → real `Format` + `ProgramSlot` rows,
      Weekly Program updates live (US5; manual step 5).
- [x] Any malformed model output → tolerant parse, bounded retry with corrective system message,
      System failure line at worst; zero unhandled exceptions in the turn path (US2; tests 9.1/9.2).
- [x] "Charlie, do a podcast with Jenny" → real A↔B agent exchange with Jenny's own context,
      unprompted Jenny→admin message, terminal report + planned-segment artifact per D5 (US6).
- [x] Exchanges hop-capped at `ChatMaxAgentHops`, terminal-action enforced, every hop visible in
      the A↔B channel and the Console log (US6).
- [x] A real production failure (booth down) produces a System message in the station channel
      within seconds (US7; manual step 9).
- [x] `dotnet build WhipRadio.slnx` clean; full test suite green; ARCHITECTURE.md +
      DESIGN-GUIDE.md updated.
