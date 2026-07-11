# Native agent pane UX polish

**Status:** in progress

A polish pass over the native structured-agent pane (`AgentPane`/`AgentComposer`, provider `codex`),
closing the day-one UX gaps against the embedded Claude Code TUI. Each iteration is a self-contained
change set, reviewed and proven with a recorded run before the next begins. Everything here is
provider-neutral: the web renders pane messages and the command catalog, never a Codex-specific concept.

## 1. Turn progress feedback

The pane gave no lifecycle feedback while a turn ran: no working indicator, no elapsed time, a
"Run" button that silently became a steer, and a permanently-visible disabled Interrupt button.

- **Working row** (`.agent-working`, top of the composer): spinner + "Working" + elapsed time
  ticking per second + "<Key> to interrupt" hint. State derives entirely from the pane's message
  stream (`turn-progress.ts`: `hasActiveTurn`), so it replays correctly on reconnect; the clock
  baselines when this view sees the turn begin (the protocol carries no turn timestamp) and
  re-baselines on session switch.
- **Blocked-on-you state**: while the newest approval/input request is unresolved
  (`pendingRequestKind`), the row turns amber and reads "Waiting on your approval" / "Waiting on
  your answer" — a running spinner would misreport who is being waited on.
- **Steer affordance**: during an active turn the submit button reads "Steer" (the host really
  steers mid-turn via `turn/steer`); "Sending…" while a submission is in flight; "Run" otherwise.
- **Interrupt button** renders only while a turn is interruptible instead of sitting disabled.
- Key hints come from the command catalog (`keyLabel`, sibling of `keyHint`) and re-resolve when
  keybindings change; nothing is hardcoded.
