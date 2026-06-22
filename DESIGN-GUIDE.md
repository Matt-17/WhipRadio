# WhipRadio Web Design Guide

This guide keeps the Blazor console visually coherent. Use it before changing
`src/WhipRadio.Web` pages, components, or `wwwroot/app.css`.

## Design Direction

WhipRadio is a late-night FM broadcast console, not a generic admin dashboard.
The interface should feel like a working studio rack: dense, readable, warm,
slightly analog, and built for repeated operator use.

Keep the current theme:

- Warm studio black backgrounds: `--bg-0`, `--bg-1`, `--bg-2`, `--bg-panel`.
- Amber VFD/control color for primary focus, active controls, and selected rows.
- Red only for on-air alerts, recording/live danger, destructive actions, and
  failures.
- Green for good runtime state, successful status, healthy meters, and positive
  votes.
- Mono labels and readouts for controls, timestamps, status, tables, and logs.
- Display font only for station identity, page titles, hero readouts, host names,
  artist names, and large stats.

Avoid adding new decorative themes, pastel palettes, marketing hero sections,
large empty landing-page layouts, floating cards inside cards, or unrelated
gradient/orb decoration.

## Typography

Use the three existing font tokens only:

- `--font-body`: default reading text, descriptions, explanatory copy, bios,
  personas, and longer prose.
- `--font-mono`: operator controls, labels, buttons, inputs, selects, tables,
  logs, timestamps, status tags, meters, technical values, URLs, transcripts,
  and compact readouts.
- `--font-display`: station identity, page titles, VFD titles, host names,
  artist names, history/detail titles, and large stat values.

Rules:

- Do not introduce page-local font families or raw font-family stacks. Add or
  reuse named CSS classes that reference the font tokens.
- Do not use display font for dense controls, tables, forms, or paragraph copy.
- Do not use mono font for long prose unless the content is a transcript, log,
  prompt, code-like value, URL, or technical readout.
- Keep font size tied to component class, not inline styles. If a view needs a
  smaller note, larger value, or compact row, create a named class for that
  pattern.
- Preserve tabular numeric alignment for counts, durations, timestamps, meters,
  and table numeric columns.
- Letter spacing belongs on short uppercase labels and display/radio readouts;
  avoid adding it to body copy or long text.

## Page Inventory

All current pages should stay within one of these patterns:

| Page | Role | Primary design pattern |
| --- | --- | --- |
| Live Broadcast | Listener/operator front page | VFD now-playing panel, VU meter, queue table, compact greeting form |
| Record Collection | Music library | Sticky artist rail, dense track grid, modal creation/confirmation |
| Play Log | Broadcast history | Console table plus expandable transcript/talk rows |
| Listener Messages | Message moderation | Segmented filter, console table, row actions |
| Weekly Program | Schedule view | Horizontal schedule grid inside one panel |
| Show Formats | Format management | `mod-grid` cards with vote buttons and trailing enable switch |
| Branding | Station identity and jingles | Form panels plus library-style jingle rows |
| Hosts | Host roster | Master-detail layout, tags, host portrait, modal creation form |
| News | Production settings | `admin-row` enablement, forms, feed table, feed modal |
| Weather | Production settings | `admin-row` enablement plus form panels |
| Studios | Studio operations | Sectioned studio tables, live status tags, inline creation/edit form |
| Studio History | Operational audit | Full-height filter/list/detail dashboard |
| Mixer | Audio engine operations | `admin-row` enablement, live stat cards, settings, transition table |
| Stats | Metrics | `stat-grid` and compact summary tables |
| Console | Logs | Full-height toolbar plus scrollable log readout |
| Server | Machine status | Meter rows and compact tables |
| Admin | Master controls | `admin-row` enablement and pacing form |
| Settings | Station configuration | Stacked form panels with one final save action |

## Layout Rules

- Keep `MainLayout` as the frame: masthead, rack navigation, stage, persistent
  footer player.
- Use one `h1.stage-title` and one `div.stage-sub` at the top of normal pages.
  Full-height dashboards may wrap these in their dashboard root.
- Use `panel` for framed work surfaces. Do not nest generic `panel` sections
  inside other generic panels unless the existing pattern already requires it
  for stat cards or a dashboard sub-pane.
- Prefer dense, scannable operator surfaces over marketing composition.
- Keep top-level page blocks aligned to the existing vertical rhythm. Use
  `panel`, `stat-grid`, `stat-columns`, `mod-grid`, `host-layout`, or a named
  page layout rather than ad hoc wrappers.
- Use full-height dashboard layouts only for surfaces that need continuous
  scanning or split-pane work, such as Console and Studio History.

## Controls

### Enable/Disable Switches

Use switches for binary active/off or enabled/disabled state:

- Service or production enablement: playout, music production, greetings, news,
  weather, mixer, studios, formats, feeds.
- Active/inactive membership: feed active, studio active, format enabled.
- Jingle active/off state.

Do not model people/hosts as enable-disable switches. Host lifecycle should be a
deliberate action such as fire/remove, with any rehire flow handled as a later
feature.

Placement:

- In settings/control panels, use `admin-row`: label and explanation on the
  left, switch on the far right.
- In tables/cards, place the switch in the trailing action/status area.
- A switch is always the last item in its row or action cluster.

Behavior and accessibility:

- Switches always save immediately. If a page needs an Apply/Save button, the
  switch does not belong in that staged form flow.
- Switches support two sizes through an explicit `SwitchSize` choice: normal
  is the default, and big is allowed for prominent control-room rows.
- Switches may have an optional tone. Default on-state is amber; the On Air
  playout switch uses a red tone to match broadcast/live semantics.
- Switches should expose state with `role="switch"` and `aria-checked`.
- They need a useful `title` or visible label describing the target.
- Keyboard support should be preserved or added when unifying switch markup.
- Amber/on means enabled. Red/on is reserved for On Air/broadcast-live state and
  other explicitly live-dangerous controls. Muted/off means disabled.

Use a select when the value has more than two options. Boolean settings that
currently use `enabled/disabled`, `on/off`, or `true/false` selects should move
to immediate switches — **unless** they live in a staged form panel with an
Apply button, in which case they stay as selects.

### Segmented Controls

Use `seg` plus `seg-btn` for mutually exclusive local view modes and filters:

- Message kind filter.
- Greeting/request selection.
- Console log level filter.
- Studio source mode.

Segmented controls should use `role="radiogroup"` with an accessible label when
the visible label is not enough. Do not use segmented controls for persistent
backend enablement.

### Buttons

- Use `btn primary` for the main commit action in a panel or modal.
- Use plain `btn` for secondary actions.
- Use `btn small` in table rows, footer controls, dense action groups, and
  pagination.
- Use `btn danger` or red hover styles only for destructive actions.
- Use `btn icon-only` only when the icon is familiar and the button has a
  `title`; otherwise include text.
- Keep icon buttons compact and stable in size. Prefer `Icon.razor` over new
  inline SVGs unless extending the shared icon component first.
- Use one button size per action row. Do not mix normal and small buttons in
  the same row.
- Button importance changes through `primary`, `danger`, or active state, not
  through a different size.
- Large or custom-size buttons belong only in persistent chrome such as the
  footer player.

### Forms

- Use `form-grid` for grouped settings and creation forms.
- Wrap every input/select/textarea in `field` with a label.
- Keep form labels short, uppercase via CSS, and operator-facing.
- Use selects for enumerated values; use number inputs for numeric settings.
- Place save/apply actions in `btn-row` below the fields.
- Use `flash` for successful short feedback and muted text for non-blocking
  errors or notes. Use red/danger styling only for real failures.

### Tables, Lists, And Dashboards

- Use `console-table` for tabular operational data and history.
- Use `StatusBadge` for compact status/state metadata such as `Active`,
  `Pending`, `Queued`, `REC`, `Recording`, `Off`, `Failed`, `On air`, and
  `Checking`.
- Plain non-state metadata such as category, kind, genre, language, provider, or
  voice labels may keep using passive tag styling.
- Numeric columns should use `num` and tabular alignment.
- Use custom named grids for high-density non-table content such as library
  tracks, schedule blocks, host rosters, and history panes.
- In the Record Collection artist track grid and Branding jingle library, the
  in-progress recording row's action cell is the exception to the `StatusBadge`
  rule: it must use the compact red-dot `rec-cell` indicator, with no `REC`
  text, and match the exact size of the play buttons in rows below.
- Empty/loading states should use `empty-state`; add a blinking glyph only when
  it fits the console/radio language.

### Status Badges

Status/state badges must be rendered through a shared component, not hand-coded
page-local spans. Use a `StatusBadge` component for any state that can change or
drive operator decisions.

`StatusBadge` should own:

- Text normalization and no-wrap sizing.
- Tone mapping: active/ready/succeeded/on-air as green; queued/pending/checking
  as amber; recording/REC/busy/failed/error as red; off/inactive/unknown as the
  muted default.
- Optional live/recording dot or pulse for `REC`, `Recording`, and busy studio
  work.
- Tooltip/title pass-through for detail such as failure reason or runtime
  explanation.
- In Rec status panels/controls (including REC buttons), use a larger bull marker
  than standard status bullets to make recording state visually dominant. Use
  the normal `&bull;` style for all non-REC status bullets.

Pages should not create new status colors, raw `tag green`/`tag amber`/`tag red`
state spans, or custom REC badges. If a page needs a new state label, add it to
`StatusBadge` instead.

### Modals

- Use `Modal` for editing/creation flows and `ConfirmDialog` for yes/no
  confirmation.
- Use `modal-wide` for form-heavy dialogs.
- Put destructive confirm buttons in danger style.
- Keep modal footers right-aligned except for destructive delete actions, which
  may sit left to separate them from save/cancel.
- Modal footers use one button size across the whole footer.
- Repeatable long-running actions launched from a modal should close after the
  request is accepted, create a visible queue/pending item in the owning page,
  and surface progress, success, or failure inline with that item. Examples:
  artist creation, song creation, and general host creation.
- One-time setup actions may keep the modal open while work runs when the user
  normally needs only one result. Examples: creating a Weather Host or News Host.
  During that wait, disable the create button, keep the label on one line, add a
  yellow/amber border, and change the button text to a working state such as
  `Creating...`.
- Keep the modal open for quick validation errors or failed acceptance where no
  background work was queued.

## Status Semantics

Use colors consistently:

- Amber: active focus, queued, selected, pending, current, enabled.
- Green: ready, healthy, succeeded, on air, positive.
- Red: failed, destructive, recording, busy, dangerous restart/delete.
- Muted/default: inactive, off, unknown, secondary metadata.

Operator truth is more important than optimistic polish. Pages that show runtime
state should reflect actual reachability/status, not just saved settings.

## Copy And Tone

- Page titles are concise nouns: `Mixer`, `Server Stats`, `Studio History`.
- Subtitles can carry the radio-console voice, but should still explain the
  operator purpose.
- Labels should be short and concrete.
- Avoid instructional text that explains obvious UI mechanics.
- When using bullet glyphs in UI copy or compact labels, use `&bull;` / `•`
  (Windows `Alt+0149`), not smaller dot variants.
- Keep status messages compact: `queued`, `recording`, `offline`, `checking`,
  `applied`, `failed`.

## Responsive Behavior

- Preserve the current desktop-first operator console density.
- On smaller screens, collapse rails/grids into one column using existing media
  query patterns.
- Fixed-height/continuous-readout areas should define stable dimensions so live
  text, progress, and buttons do not shift layout.
- Tables and schedule/history views may scroll horizontally when density is
  necessary.

## Current Competing Patterns

The current UI is not fully unified. Treat these as known inconsistencies for
the next design cleanup:

- Immediate service switches: Admin playout, music production, greetings, Mixer
  enablement, and row-level studio/feed/format toggles save immediately.
- Staged settings selects: News and Weather production pages use on/off selects
  for enablement and extraction/handover because those fields are saved by an
  Apply button. This is intentional — a switch must never sit in a staged form.
- Boolean selects: Settings and some production forms use `off/on` or
  `disabled/enabled` selects for boolean settings outside staged forms. These
  should become switches.
- Active buttons as toggles: footer transcript toggle, Console pause/order, and
  some row expanders use button active state instead of a switch. The footer
  transcript toggle stays as a button because the transcript icon communicates
  purpose more clearly than a bare switch would — a switch needs text to
  explain what it controls, while the icon-only button does not. The checked
  state uses a bright amber fill so on/off is visually obvious. Pause/resume
  and sort/order controls remain toggle buttons. Jingle active/off uses a
  switch.
- Status badge drift: active, pending, queued, REC/recording, failed, and
  offline states are hand-coded with raw `tag` spans across pages. These should
  be consolidated into `StatusBadge`.
- Button sizing drift: some rows mix normal and small buttons, especially form
  and modal action rows. These should use one size per row.
- Blocking modal drift: any modal action that starts slow generation,
  production, import, analysis, or remote work should follow the repeatable vs.
  one-time modal rule above instead of leaving the user without clear state.
- Font sizing drift: pages contain inline `font-size` and occasional inline
  font-family styles for notes or textareas. These should become named CSS
  classes using the existing font tokens.
- Accessibility drift: News feed switches include `role="switch"` and
  `aria-checked`; several other visual switches do not yet expose the same
  semantics.
- Inline style drift: some pages still carry local layout styles that should
  become named CSS classes when those views are cleaned up.

## Known Cleanup Targets

These are guidance points for a later unification pass, not changes to make
while only updating this guide:

- Normalize all switch markup to the same accessible pattern, with explicit
  `SwitchSize` and optional tone.
- Remove page-local inline styles when a reusable CSS class would preserve the
  same layout.
- Convert boolean selects to immediate switches **outside** staged form panels.
- Convert jingle active/off from an active button to a switch.
- Introduce `StatusBadge` and replace page-local status/state tag spans with it.
- Normalize action rows to one button size per row.
- Convert repeatable slow modal submit flows to close-on-accept plus visible
  queued/pending items. Convert one-time specialist creation modals to disabled,
  no-wrap, amber-bordered working buttons while the modal stays open.
- Replace inline font styles with named typography classes.
- Prefer shared `Icon.razor` icons for action buttons instead of adding new
  inline SVGs or mixed symbols.
- Keep row-level queued/deferred state inline with the affected row rather than
  duplicating it in a separate banner.
