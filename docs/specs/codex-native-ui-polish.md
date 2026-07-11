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

## 2. Approvals: show the substance, answer from the keyboard

An approval card showed only the provider's *reason* (often empty) — the user consented blind — and
its buttons were mouse-only, in a keyboard-first product.

- **What you're approving**: `CodexPaneMessages.FromRequest` now maps the request's substance into
  `Text` — the exact command for `item/commandExecution/requestApproval`, the changed paths for
  `item/fileChange/requestApproval` — rendered in the card's monospace block. The reason stays the
  summary line.
- **Keyboard decisions are commands** (`weavie.agent.approve` / `approveForSession` / `decline`,
  default `Alt+Y` / `Alt+Shift+Y` / `Alt+N`), gated `agentFocused && agentApprovalPending` so the
  chords exist only while a card is pending. They answer the **newest** unresolved approval — the
  same request the working row says it is waiting on. Alt-chords keep plain typing free because
  steering mid-approval stays a first-class input. Cancel-turn needs no chord: Escape already
  interrupts.
- **Buttons advertise the chords** with visible key chips (resolved live from the catalog), so the
  mouse path teaches the keyboard path.
- **Input-request cards submit on Enter** (the questions now live in a form), matching every other
  text field in the app.

## 3. First-contact discoverability

The idle pane said one dim sentence and the composer placeholder taught nothing.

- **Empty-state welcome** (`EmptyState` in `AgentTranscript`): the provider's name, what the pane is
  for, and a hint table teaching the keyboard paths — submit/steer, `/` commands, `↑` history,
  interrupt — plus a pointer at the control strip. Rebindable actions read the catalog live;
  `/` and `↑` are intrinsic composer behaviors, so their glyphs are fixed.
- **Placeholder teaches the slash menu** ("Write a prompt — / for commands and skills") and flips to
  "Steer the running turn…" mid-turn, reinforcing that submitting steers.

## 4. Transcript & control details

- **Follow pill**: scrolling up pauses follow (existing behavior) but gave no way back short of
  manually scrolling; a floating "↓ Jump to latest" pill now appears whenever follow is paused and
  one click re-sticks.
- **Control picker header** names the axis being picked (Model / Approvals / Sandbox).
- **Attachment chips** say "reading… / uploading… / failed" instead of raw state names, and a ready
  thumbnail carries no badge at all.
