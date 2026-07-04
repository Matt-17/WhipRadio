# WhipRadio - Phase X: Split Tool Routing And Writing Models

> Design brief. WhipRadio should not make one local LLM do every kind of
> thinking. Tool routing is a small, strict, low-latency classification problem;
> host/director writing is a larger persona and reasoning problem. Split those
> jobs so each model is used where it is strongest.

---

## 1. Goal

Introduce a two-model text architecture:

| Model role | Suggested class | Job |
|---|---|---|
| Tool Router | FunctionGemma-style tiny tool model, or another small local model | Decide whether a message needs a tool, which shortlisted tool it maps to, and typed arguments. |
| Writer / Reasoner | Current larger Writer Room model such as `gemma4:e4b` | Persona, explanations, program-director reasoning, scripts, final chat replies, and creative text. |

The small model is **not** the station brain. It is an intent parser and tool
proposal engine. The Orchestrator remains the authority for permissions, state,
selection, scheduling, queueing, and all side effects.

---

## 2. Core Principle

Never execute from prose.

Only execute a validated structured tool proposal:

```json
{
  "intent": "tool",
  "confidence": 0.91,
  "tool": "SearchArtist",
  "arguments": {
    "style": "dark late-night synthwave duo",
    "genre": "electronic",
    "createIfMissing": true
  },
  "needsConfirmation": false
}
```

Free-form reasoning, explanations, and "thoughts" from any model are advisory
only. C# validation and existing station services decide whether anything
happens.

---

## 3. Recommended Flow

Normal chat/tool turn:

```text
Admin / agent message
  -> build compact routing context
  -> small Tool Router model proposes no tool or one/more tool calls
  -> deterministic validator checks role, schema, target, state, and guardrails
  -> executor runs allowed tools through existing services
  -> larger Writer model writes the final reply from facts and tool results
```

The current Phase 4 loop already does:

```text
large model -> reply + actions -> validate -> execute -> feed back -> final reply
```

Phase X changes that to:

```text
small model -> actions only
large model -> final reply only
```

This keeps tool JSON tight and makes the visible chat reply better.

---

## 4. Temperature And Determinism

Tool routing should use `temperature = 0` or the closest supported greedy
setting.

This means the model is **more deterministic**, not absolutely deterministic:

- same prompt/model/options usually gives the same output;
- quantization, runtime, model version, prompt order, and sampler details can
  still change behavior;
- deterministic validation in code remains mandatory.

The Writer model can keep a higher temperature for personality, banter, and
creative scripts. Tool selection should not inherit that creativity.

---

## 5. What The Small Model May Decide

Allowed:

- "This message is only informational; no tool."
- "This message asks for `StatusReport`."
- "This message asks for `SearchMusic`."
- "This message asks for `SearchArtist` with these style constraints."
- "This message asks for `Message(targetType=ProgramDirector, ...)`."
- "This action likely needs Boss confirmation."
- "Confidence is low; ask a clarifying question."

Not allowed:

- Choose the actual next rotation track from scratch.
- Bypass the `WeightedTrackSelector`, format rules, repeat windows, retire
  flags, queue state, or current playout state.
- Decide personnel authority.
- Decide whether a destructive action is safe.
- Write or mutate station entities directly.
- Interpret broad prose as approval for destructive actions.

The small model proposes intent. The station executes policy.

---

## 6. Song Selection Boundary

For "what song should play next?", the split is:

```text
Small model:
  "This is a music request with mood=dark, genre=electronic."

Station services:
  resolve allowed tracks, apply format rules, repeat windows, active/retired
  filters, queue constraints, listener request rules, and playout timing.

Writer model:
  explain what happened in character after the tool result exists.
```

Example tool proposal:

```json
{
  "intent": "tool",
  "confidence": 0.88,
  "tool": "RequestNextTrackBrief",
  "arguments": {
    "mood": "dark",
    "energy": "medium",
    "genre": "electronic"
  },
  "needsConfirmation": false
}
```

If the user names a precise track, the router may propose a specific
`QueueTrack(trackId=...)` only after server-side exact resolution. Even then,
the queue service decides whether it can actually be queued.

---

## 7. When To Use The Larger Model Before Routing

Most turns should go directly to the Tool Router. Do **not** ask the large model
to write full chain-of-thought and then parse that prose.

For ambiguous or strategic requests, the large model may produce a short
structured decision brief first:

```json
{
  "summary": "The user wants a new dark synthwave artist and maybe a song.",
  "recommendedAction": "discover_artist",
  "constraints": [
    "hosts may discover artists",
    "song production belongs to artists or production services",
    "no schedule change requested"
  ],
  "missingInfo": []
}
```

Then the Tool Router maps that brief to exact tool JSON.

Use this slower path only when the router marks low confidence or when the
request spans multiple domains such as schedule, personnel, artists, and
playout.

---

## 8. Tool Shortlisting

The router must never see the full `TOOLS.md` catalog in a normal turn.

Build a compact shortlist from:

- prompt scope;
- actor role;
- channel type;
- addressed target;
- current station state;
- message classification hints;
- whether a tool requires confirmation.

Typical shortlists:

| Situation | Shortlist |
|---|---|
| Host DM, casual message | `Message`, `Announcement`, `SearchMusic`, `SearchArtist`, `StatusReport` read-only if exposed |
| Program Director DM | `StatusReport`, `PlanFormat`, `HireHost`, `AssignHost`, `SearchArtist`, `Message` |
| Artist DM | `Message`, `CreateSong`, `PostArtistFeed`, `GetArtistProfile` |
| Boss asks "what is happening?" | `StatusReport`, `StudioStatus`, `PrivacyReport`, `ServerStatus` |
| Destructive request | matching tool plus `RequestBossApproval`, never direct execution without confirmation |

The prompt should include only names, descriptions, typed parameters, and
role-specific limits for those shortlisted tools.

---

## 9. Router Output Contract

Prefer a strict schema like:

```json
{
  "intent": "none | tool | clarify | refuse",
  "confidence": 0.0,
  "replyHint": "short optional note for the Writer model",
  "toolCalls": [
    {
      "tool": "ToolName",
      "arguments": {},
      "needsConfirmation": false,
      "why": "short non-secret reason"
    }
  ],
  "clarifyingQuestion": ""
}
```

Rules:

- `intent=none` means no side effect; Writer model replies normally.
- `intent=clarify` means no tool executes; Writer asks the question.
- `intent=refuse` means no tool executes; Writer explains the refusal.
- `confidence` below the server threshold must fail closed to clarify or large
  model review.
- `needsConfirmation` is a model hint only; the server recomputes confirmation
  requirements.

---

## 10. Per-Tool Typed Schemas

The current chat schema uses a permissive string map for `arguments`. The split
works better with typed per-tool schemas.

Desired router schema:

- `tool` is one of the shortlisted names;
- each tool has typed arguments;
- enum values are explicit;
- required fields are explicit;
- unexpected properties are rejected or ignored before execution.

Examples:

```json
{
  "tool": "Message",
  "arguments": {
    "targetType": "ProgramDirector",
    "targetName": "",
    "message": "Can you handle this schedule request?"
  }
}
```

```json
{
  "tool": "SearchArtist",
  "arguments": {
    "style": "smoky trip-hop duo",
    "genre": "electronic",
    "subgenre": "trip-hop",
    "createIfMissing": true
  }
}
```

The runtime still converts to the existing `CharacterToolCall` shape or evolves
that shape to typed payloads later.

---

## 11. FunctionGemma Fit

FunctionGemma-style models are a good candidate for the Tool Router because the
task is small:

- classify intent;
- choose from a small tool shortlist;
- fill typed arguments;
- report confidence;
- stay at low temperature.

Do not use the tiny model as the Program Director brain:

- multi-turn planning still belongs to the larger Writer/Reasoner;
- host persona and emotional continuity belong to the larger model;
- on-air scripts need the larger model's language quality;
- tool execution remains deterministic code either way.

FunctionGemma can be swapped out behind the same `IToolDecisionService` if a
different small model performs better.

---

## 12. Proposed Services

### `IToolDecisionService`

Input:

- actor role and id;
- channel facts;
- last message;
- compact station state;
- shortlisted tool definitions;
- optional structured decision brief;
- max tool calls.

Output:

- intent;
- confidence;
- normalized tool calls;
- validation errors;
- optional clarifying question.

### `ToolDecisionRouter`

Responsibilities:

- build role/channel-aware shortlist;
- call the small model;
- parse strict JSON;
- apply confidence threshold;
- map to `CharacterToolCall`;
- never execute actions directly.

### `ToolDecisionValidator`

Responsibilities:

- check tool availability by role and prompt scope;
- check typed schema;
- resolve targets exactly;
- recompute confirmation requirement;
- reject dangerous combinations;
- produce operator-readable validation errors.

### `ChatAgentTurnService` changes

Current:

```text
large model -> parse reply/actions -> execute -> maybe loop -> post final reply
```

Target:

```text
router shortlist -> small model action proposal -> validate -> execute
  -> large model final reply with tool results -> post reply/actions
```

Lookup actions such as `SearchMusic`, `SearchArtist`, and `StatusReport` still
feed their results into the final reply.

---

## 13. Configuration

Add a separate options section:

```json
{
  "Llm": {
    "Model": "gemma4:e4b",
    "ContextSize": 16384,
    "Temperature": 0.8,
    "ToolModel": "functiongemma:latest",
    "ToolContextSize": 4096,
    "ToolTemperature": 0.0,
    "ToolConfidenceThreshold": 0.75
  }
}
```

Notes:

- names above are placeholders until the local Ollama tags are verified;
- existing `Llm__Model=gemma4:e4b` remains the Writer default;
- tool model residency must respect studio resource coordination;
- if no tool model is configured, fall back to the current single-model path.

---

## 14. Failure Modes And Fallbacks

| Failure | Behavior |
|---|---|
| Tool model unreachable | Fall back to current large-model chat action path, or reply that tools are unavailable if configured strict. |
| Invalid router JSON | Retry once with correction; then fail closed to no action. |
| Low confidence | Ask a clarifying question or ask large model for a structured decision brief. |
| Unknown tool | Reject before execution and log. |
| Missing required argument | Reject before execution; Writer explains what is missing. |
| Ambiguous target | Reject and ask for exact target. |
| Confirmation required | Store pending action; do not execute until Boss confirms. |
| Tool result failed | Feed failure to Writer; final reply must admit the failure. |

---

## 15. Security And Authority Guardrails

- The small model never grants itself permissions.
- The small model never changes confirmation requirements.
- The small model never chooses final station state.
- The executor never trusts model prose.
- Every tool result is logged through existing agent/action log paths.
- Destructive actions require exact ids and confirmation where documented in
  `TOOLS.md`.
- The Program Director can manage shows and hosts; hosts cannot.
- Artists can post and create songs only as themselves.
- System messages stay informational.

---

## 16. Implementation Milestones

### M1 - Decision DTOs and schemas

- Add `ToolDecision`, `ToolDecisionCall`, and typed argument DTOs.
- Add strict JSON schema generation for router output.
- Add unit tests for parse failure, confidence thresholds, and malformed data.

### M2 - Model routing configuration

- Extend `LlmOptions` with tool-model fields.
- Add a named small-model Ollama client or a model override on requests.
- Keep existing Writer Room endpoint; do not introduce a new studio unless
  operations require it.

### M3 - Shortlist builder

- Build tool shortlist from role, scope, channel, and current station state.
- Ensure normal prompts never include the full tool catalog.
- Add availability matrix tests matching `TOOLS.md`.

### M4 - Tool decision service

- Implement `IToolDecisionService`.
- Use `temperature=0` and small context.
- Retry invalid JSON once.
- Fail closed on low confidence or invalid output.

### M5 - Chat turn integration

- Update `ChatAgentTurnService` so tool proposal and final prose are separate.
- Preserve in-turn lookup behavior.
- Preserve hop caps, terminal Admin reports, action logs, and system failure
  behavior.

### M6 - Station-decision boundaries

- Add `RequestNextTrackBrief` or equivalent so the router can express "play
  something darker" without picking a track itself.
- Route final selection through existing track selection and queue services.
- Add tests proving the router cannot bypass retired-track, duplicate, and queue
  constraints.

### M7 - Observability

- Log router model, confidence, chosen tool, validation result, and fallback path.
- Surface concise action records in chat.
- Keep raw prompts out of normal UI unless an operator diagnostics view is added.

---

## 17. Done When

- Tool routing can use a configured small local model.
- The larger model writes final replies and creative content, not first-pass tool
  JSON.
- Router output is strict JSON with confidence.
- Shortlists are role/channel-aware and usually fewer than 8 tools.
- Low-confidence or invalid router output never executes.
- A request like "play something darker next" becomes a constrained station
  request, not arbitrary model-picked playback.
- Exact named-track requests still require server-side exact match and queue
  validation.
- Existing Phase 4 guarantees remain intact: malformed output does not crash,
  role permissions are enforced, agent-to-agent exchanges are hop-capped, and
  every side effect goes through existing Orchestrator services.

---

## 18. Open Questions

- Which tiny model performs best locally: FunctionGemma, a small Gemma variant,
  or another Ollama model with strong JSON behavior?
- Should the Tool Router run in the same Ollama container as the Writer model,
  or a separate Writer Room endpoint for residency control?
- Should ambiguous requests call the larger model for a structured decision
  brief before routing, or should the router ask the Boss to clarify first?
- What confidence threshold gives the best balance between responsiveness and
  false positives?
- Should OpenAI/native tool calling be an alternate `IToolDecisionService`
  backend later, while preserving the same validator and executor?
