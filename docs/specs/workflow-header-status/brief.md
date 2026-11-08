# Workflow run in the agent pane header

## Goal

A user looking at a session's agent pane can see, without leaving it, which workflow is running, which stage it is at,
and whether it needs them. This settles the "detail on demand" question in
[workflows.md](../workflows.md#visualizing-a-run) for now with a glanceable line, not an expandable header.

## Behavior

While the selected session has a live run, the pane header keeps its label on the left and shows
`● <workflow> · <stage>` right-aligned. With no live run the header is unchanged.

Both agent headers show it, through one shared component, because workflows run on either:

- the native pane's "Claude" header (`src/web/src/agent/AgentPane.tsx`)
- the terminal agent's "Claude Code" header (`src/web/src/App.tsx`)

### Dot

A plain 8px dot: no pulse, no halo.

| Run state | Color |
| --- | --- |
| `working`, `deciding`, `watching` | `--busy` |
| `waitingOnYou` | `--ok` |
| `needsAnswer`, `paused` | `--warn` |
| `stalled` | `--bad` |

`waitingOnYou` stays calm because it only means the agent is idle in a stage, and the session list doesn't count it as
needing the user. Amber means the run is blocked on them.

### Text

Same font and size as the label, regular weight, `--dim`. It doesn't take the accent color when the pane is focused.

### Tooltip

`ship-task · review · needs your answer`, using the state phrases from `src/web/src/chrome/session-summary.ts`. When the
run has a reason (a pause reason or pending question), it follows on a second line.

### Layout

- With the `Ctrl+N` shortcut badge showing, the run sits just left of it.
- In a narrow pane the label never shrinks. The workflow name truncates first with an ellipsis, so the stage stays
  readable as long as possible (`feat… · implement-and-test`). The tooltip has the full text.

## Scope limits

- Compact (mobile) layout: not shown. The session inbox already gives stage and state.
- The status line under the composer is unchanged, and the PR stays there.
- A saved run that couldn't be loaded shows nothing in the header. The session list already reports it.
- The line is display only: no click action, command, or keybinding. Clicking it behaves as clicking the header does.

## Out of scope

- A stage count such as `2/6`: `if` skips and `then` loops make it misleading.
- The yellow Question card in `temp/mockups/final-header-and-card.png`. Questions are answered in conversation with the
  agent. Only that mockup's header applies.
- An expandable run header or any other run detail view.
- Host or wire changes: the client already receives the run on the session's `status` feed.
- Any other change to the standard UI.

## Mockups

`temp/mockups/header-options.png` (gitignored) shows the agreed badge placement, truncation, weight, and dot colors.
Its section B shows end truncation, which this brief replaces with workflow-first truncation.
