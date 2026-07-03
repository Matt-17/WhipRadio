# WhipRadio — Phase 4 Brief: Chat Control

> Design brief. This is the phase that turns WhipRadio from a configured system into a
> *directed* one — you talk to it like a team. Firmest part is the action protocol;
> openest part is how rich the multi-agent behaviour gets (that bleeds into Phase 5).
>
> Depends hard on 3b's `PromptContextBuilder` — chat agents are only tractable because
> every message can carry consistent facts.

---

## 1. Goal

A WhatsApp-like Chat page: a **station group chat** plus **per-host (and later
per-artist) DMs**. Messages don't just produce replies — they **trigger actions**: a
host produces an announcement on request, the Program Director plans a Friday slot,
hires a host, etc. The system also **proactively** messages you (failures, format
wrap-ups, host-to-host coordination results).

---

## 2. The action protocol (the firm core)

Local LLMs don't all have native tool-calling, so use a **self-built, parse-tolerant
action protocol** — exactly the user's instinct. The agent emits free text and/or
action calls; a parser extracts and dispatches them.

### Output shape
A reply may mix prose and actions, e.g.:
```
Klar, mach ich!
Announcement(priority=high, topic="Die Kuh steht im Wald")
Nachricht(Admin, "Kommt sofort auf Sendung")
```

### Parsing (firm rules)
- Define a small, explicit verb set with a stable signature each (see §3). Document them
  in the system prompt via the `PromptContextBuilder` (the verbs available depend on
  *which agent* is speaking — a host can't re-plan the week; the director can).
- The parser is **tolerant**: case-insensitive verb match, tolerant of quote styles and
  minor arg spacing, ignores unknown verbs (logs them). Never crash on a malformed call.
- Each parsed action is **validated** against the agent's permissions and the args'
  schema before dispatch. Invalid → the agent gets a system message back describing the
  error so it can retry (this loop is what makes it robust).
- Every action dispatch and result is logged and (where relevant) surfaced in chat.

**Open Choice:** plain-text verb syntax (above) vs a fenced JSON block. Recommend
plain-text verbs for local-LLM reliability, but isolate parsing behind `IActionParser`
so a JSON or native-tool-calling backend can replace it when using OpenAI. The contract
(`ParsedAction { Verb, Args, RawText }`) stays the same.

---

## 3. Action catalogue (starting set — extend as phases land)

Permissions are per agent role. Examples:

| Verb | Who | Effect |
|---|---|---|
| `Nachricht(to, text)` | all | send a chat message to a participant |
| `Announcement(priority, topic, …)` | hosts | produce + queue an announcement in their voice |
| `SucheMusik(genre/mood)` | hosts, director | query the library (returns results to the agent) |
| `PlaneFormat(day, time, …)` | director | create/modify a `Format` + schedule slot |
| `StelleHostEin(brief)` | director | create a `Moderator` (persona, voice, gender…) |
| `WeiseHostZu(formatId, hostId)` | director | assign an existing host to a format |
| `Statusbericht()` | director | summarise current programme state |

Each verb maps to an existing service method from earlier phases — the chat layer is a
thin, permissioned front-end over capabilities you already built. **Firm rule:** actions
go through the same services the autonomous system uses; no parallel code paths.

---

## 4. Chat architecture

- **Channels:** one `station` group + one DM channel per host/artist. Model
  `ChatChannel`, `ChatMessage (channelId, senderId, senderKind, text, actionsJson,
  createdAt)`. Senders can be Admin (you), a host, the director, or System.
- **Delivery:** SignalR hub (you already have the pattern from Phase 2) pushes messages
  live to the Chat page. A bounded history per channel is persisted.
- **Agent turn:** when you message a host, build that host's `PromptContext` (persona +
  memory + current on-air situation + available verbs), call its LLM, parse actions,
  dispatch, and post the prose reply. The host genuinely "knows" if it's mid-show and
  whether it has time to do what you asked (3b's time math).

---

## 5. Host-to-host coordination (the spicy part)

The user picked **Option B** (real multi-agent), and the reasoning is right: separate
calls let each agent carry its own memory and facts.

**In Phase 4, build the mechanism but keep the choreography simple:**
- When host A's action is `Nachricht(B, …)`, that posts into the A↔B DM channel and
  **triggers B's agent turn** with B's own `PromptContext`. B replies (and may message
  you unprompted — "freu mich drauf!"). This is already true multi-agent.
- A coordination request ("Charlie, mach mit Jenny einen Podcast") becomes: Charlie
  messages Jenny → Jenny agrees and proposes a topic → Charlie reports back to you →
  the director (or Charlie) creates the `ConversationSegment` from 3c.
- **Firm guardrails:** cap the agent-to-agent hop count per initiating request (e.g. ≤ 6
  turns) and require a terminal action (a created segment, or a message back to Admin) so
  loops can't run forever. Log the whole exchange.

**Defer to Phase 5:** large group conversations, 5 participants, guests/artists as
chat-capable entities, and the richer "they interrupt each other on air" production.
Phase 4 proves the 1:1 agent loop and the action protocol.

---

## 6. Proactive / system messages

A simple `INotificationBus` any service can publish to; the station group chat
subscribes. Examples the user named:
- Director: "Announcement konnte nicht erstellt werden — Voice Booth war 3× belegt."
- Per-format wrap-up when a host's shift ends (short summary, ties to 3b memory).
- Generation failures, model-download events, mixer anomalies.

**Firm rule:** notifications are informational by default; only the director's messages
may carry actions (e.g. an inline "soll ich neu planen?" the user can confirm).

---

## 7. Suggested milestone spine (agent refines)
1. `ChatChannel`/`ChatMessage` + SignalR delivery + Chat page (group + DMs).
2. `IActionParser` + `ParsedAction` + tolerant parsing + per-role permission/validation.
3. Action catalogue wired to existing services (start with `Nachricht`, `Announcement`,
   `SucheMusik`).
4. Agent turn assembly via `PromptContextBuilder` (per-agent verb sets).
5. Director actions (`PlaneFormat`, `StelleHostEin`, `WeiseHostZu`, `Statusbericht`).
6. Host-to-host 1:1 loop (Option B) with hop cap + terminal-action guard.
7. `INotificationBus` + proactive system messages.

---

## 8. Definition of Done (themes)
- [ ] Group chat + per-host DMs, live via SignalR, persisted history
- [ ] Telling an on-air host to make an announcement about X produces it in *their*
      voice and airs it, respecting priority
- [ ] Telling the director to plan a slot creates a real Format + schedule entry
- [ ] Malformed action calls never crash; agent gets an error and can retry
- [ ] "Charlie, mach mit Jenny einen Podcast" results in Jenny independently agreeing
      (unprompted message) and a `ConversationSegment` being created
- [ ] Agent-to-agent exchanges are hop-capped, terminal, and fully logged
- [ ] System proactively reports a real failure into the group chat

---

## 9. Open questions
- Verb syntax: plain-text vs JSON (recommend plain-text behind `IActionParser`).
- Permission model granularity — per role, or per host capability flags?
- How much should the user *confirm* destructive director actions vs let them run?
- Where do guests/artists enter as chat entities — strictly Phase 5, or stub now?
