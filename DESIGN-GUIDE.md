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
- Switches should expose state with `role="switch"` and `aria-checked`.
- They need a useful `title` or visible label describing the target.
- Keyboard support should be preserved or added when unifying switch markup.
- Amber/on means enabled. Muted/off means disabled. Red is not the normal off
  state; reserve red for failure, destructive action, live recording, or danger.

Use a select when the value has more than two options. Boolean settings that
currently use `enabled/disabled`, `on/off`, or `true/false` selects should move
to immediate switches in a cleanup pass.

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
- Use `tag` for compact status, category, kind, or state metadata.
- Numeric columns should use `num` and tabular alignment.
- Use custom named grids for high-density non-table content such as library
  tracks, schedule blocks, host rosters, and history panes.
- Empty/loading states should use `empty-state`; add a blinking glyph only when
  it fits the console/radio language.

### Modals

- Use `Modal` for editing/creation flows and `ConfirmDialog` for yes/no
  confirmation.
- Use `modal-wide` for form-heavy dialogs.
- Put destructive confirm buttons in danger style.
- Keep modal footers right-aligned except for destructive delete actions, which
  may sit left to separate them from save/cancel.
- Modal footers use one button size across the whole footer.
- Actions launched from a modal must not leave the user waiting in the modal
  while long-running work completes. Close the modal after the action is
  accepted, create a visible queue/pending item in the owning page, and surface
  progress, success, or failure inline with that item.
- Keep the modal open only for quick validation errors or failed acceptance
  where no background work was queued.

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
- Staged service switches: News and Weather package enablement use the same big
  switch visual but are saved by an Apply button. These should become immediate
  switches.
- Boolean selects: Settings and some production forms use `off/on` or
  `disabled/enabled` selects for boolean settings. These should become
  switches.
- Active buttons as toggles: footer transcripts, jingle active/off, Console
  pause/order, and some row expanders use button active state instead of a
  switch. Jingle active/off should become a switch. Footer transcript may stay
  as-is for now. Pause/resume and sort/order controls remain toggle buttons.
- Button sizing drift: some rows mix normal and small buttons, especially form
  and modal action rows. These should use one size per row.
- Blocking modal drift: any modal action that starts slow generation,
  production, import, analysis, or remote work should close after accepting the
  request and represent the work as a queue/pending item outside the modal.
- Accessibility drift: News feed switches include `role="switch"` and
  `aria-checked`; several other visual switches do not yet expose the same
  semantics.
- Inline style drift: some pages still carry local layout styles that should
  become named CSS classes when those views are cleaned up.

## Known Cleanup Targets

These are guidance points for a later unification pass, not changes to make
while only updating this guide:

- Normalize all switch markup to the same accessible pattern used by the News
  feed switches.
- Remove page-local inline styles when a reusable CSS class would preserve the
  same layout.
- Convert staged service switches and boolean selects to immediate switches.
- Convert jingle active/off from an active button to a switch.
- Normalize action rows to one button size per row.
- Convert slow modal submit flows to close-on-accept plus visible queued/pending
  items.
- Prefer shared `Icon.razor` icons for action buttons instead of adding new
  inline SVGs or mixed symbols.
- Keep row-level queued/deferred state inline with the affected row rather than
  duplicating it in a separate banner.
