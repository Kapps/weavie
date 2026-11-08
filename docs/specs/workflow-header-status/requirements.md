# Workflow run in the agent pane header: requirements

From [brief.md](brief.md) and [overview.md](overview.md). "Run line" means `● <workflow> · <stage>` in a pane header.

## R1. See the run in the native agent pane

As a user of a native agent pane, I want the header to show the live run, so I can see where it is without leaving
the pane.

1. WHILE the pane's session has a live run, the header SHALL keep its label on the left and show the run line.
2. WHEN the run moves to another stage, the run line SHALL show the new stage.
3. WHEN the run ends, the run line SHALL disappear.
4. The run line SHALL NOT include a stage count.

## R2. See the run in the terminal agent pane

As a user of terminal Claude, I want the same run line, so a run is equally visible on either kind of agent.

1. WHILE the selected session has a live run, the terminal agent's header SHALL show the run line.
2. WHEN the user switches session, the header SHALL show the newly selected session's run, or no run line if it has none.

## R3. Dot color follows run state

As a user, I want the dot's color to tell me whether the run needs me.

1. WHILE the state is `working`, `deciding` or `watching`, the dot SHALL be `--busy`.
2. WHILE the state is `waitingOnYou`, the dot SHALL be `--ok`.
3. WHILE the state is `needsAnswer` or `paused`, the dot SHALL be `--warn`.
4. WHILE the state is `stalled`, the dot SHALL be `--bad`.
5. The dot SHALL NOT pulse or show a halo in any state.

## R4. Text stays quiet

As a user, I want the run line to stay secondary to the label.

1. The run line text SHALL use the label's font and size at regular weight, in `--dim`.
2. WHEN the pane is focused, the run line text SHALL NOT take the accent color.

## R5. Tooltip spells it out

As a user, I want the full detail on hover.

1. WHEN the user hovers the run line, the tooltip SHALL read `<workflow> · <stage> · <state phrase>`, using the session
   list's state phrases.
2. WHERE the run has a reason, the tooltip SHALL show it on a second line.

## R6. Position in the header

1. The run line SHALL be right-aligned in the header.
2. WHILE the shortcut badge shows, the run line SHALL sit just left of it, with the badge at the far right.

## R7. Narrow panes

As a user with a narrow pane, I want the stage to stay readable.

1. The pane label SHALL NOT shrink or wrap.
2. WHEN space runs out, the workflow name SHALL truncate with an ellipsis before the stage does.
3. WHEN the stage alone no longer fits, the stage SHALL truncate with an ellipsis.

## R8. Compact layout

1. WHILE the compact (mobile) layout is active, the run line SHALL NOT show.

## R9. Right session, always

1. The native header SHALL only ever show the run of the session its pane belongs to.
2. WHEN a session is reloaded with a live run, the run line SHALL show again.

## R10. Nothing else changes

1. WHILE a session has no live run, its headers SHALL look as they do today.
2. IF a saved run couldn't be loaded, the header SHALL show no run line.
3. The status line under the composer SHALL be unchanged, and the PR SHALL stay there.
4. WHEN the user clicks the run line, it SHALL behave as a click on the rest of the header.
