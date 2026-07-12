# Permission model & change tracking

Status: implemented
Last updated: 2026-07-12

How Weavie governs what embedded agents may do, and how it records their changes in a way that keeps
working no matter how much they are or aren't asking. Claude and Codex use different provider mechanisms.
This supersedes the earlier
single-`claude.permissionMode`-setting design (kept in git history): permission is now split into two
orthogonal axes, only one of which Weavie owns.

## The two axes

| Axis | Controls | Owner | Set by | Weavie's role |
|---|---|---|---|---|
| **Edit mode** | how *edits* are handled — `default` (review each), `acceptEdits` (auto-apply), `plan` (read-only) | **Claude Code** | the user, via Shift+Tab in the Claude pane | **observes** it (cannot set it at runtime — see below) |
| **Tool-call permission** | whether *non-edit* tools (Bash & other commands) auto-run | **Weavie** | the `claude.allowAllTools` setting | **enforces** it, via the hook |

They are orthogonal and never contradict: Claude's mode only ever auto-accepts *edits*; Weavie's hook owns
*everything else*. So "bypass everything" isn't a mode — it's the composition **acceptEdits + allowAllTools**.

### Why Weavie doesn't just force Claude's native mode

The obvious alternative — drive Claude's native `--permission-mode` from a Weavie setting — doesn't work for
a control you toggle while working. Claude Code's mode can only be set **at launch** (`--permission-mode`) or
changed **interactively** (Shift+Tab); there is no MCP / IPC / control path to change it mid-session, and
`bypassPermissions` isn't reachable via Shift+Tab unless Claude was launched with an enabling flag. So
"forcing the mode" would mean **relaunching Claude (losing the session) on every change**. The hook, by
contrast, reads its setting live on every call — so the axis Weavie *does* own stays fully live.

### The Shift+Tab desync, and why observing solves it

Because Claude owns the edit mode and the user can Shift+Tab it at any moment, Weavie must not keep a
*competing* mode value that could drift out of sync. Instead it **observes**: every PreToolUse hook input
carries `permission_mode`, which Weavie folds into an `ObservedPermissionMode` mirror
(`Weavie.Core.Hooks`). That mirror is the single signal for "are edits auto-applying," used to gate the
post-turn review surface and the openDiff auto-keep. There is no second source of truth to desync from.

## Enforcement: the hook is the single gate

Claude is always launched in its own `default` mode (Weavie passes no `--permission-mode` /
`--dangerously-skip-permissions`), and Weavie's PermissionRequest hook is the decision point (it fires only when a prompt would otherwise appear, so an auto-allowed tool costs nothing):

- **`claude.allowAllTools` off** → the hook passes through: Claude's normal flow (edits → its mode / the
  openDiff review; Bash → its own terminal prompt).
- **`claude.allowAllTools` on** → the hook returns `decision.behavior: allow` for every non-edit PermissionRequest
  (Bash, PowerShell, WebFetch, MCP), suppressing the prompt. Edit tools always pass through here — their permission is the
  edit mode, and a hook `allow` would override it, breaking the orthogonality.

Precedence in Claude Code is `deny > hook > ask > allow`, so a user's own `deny` rule still wins and the hook
reliably suppresses an otherwise-`ask` prompt. `HookPolicy.Decide(request, allowAllTools)` is the seam; the
live setting is read per call in `IdeIntegration`. See [../concepts/hook-bridge.md](../concepts/hook-bridge.md).

> **Coverage.** The gate is the `PermissionRequest` hook with a `*` matcher, so `allowAllTools` auto-allows
> *every* non-edit tool that would prompt (Bash, PowerShell, WebFetch, MCP). PermissionRequest fires only
> when a dialog would appear, so it never runs for auto-allowed tools; `PreToolUse`/`PostToolUse` stay scoped
> to the edit tools for change tracking. Per-tool control remains a possible refinement.

## Change tracking (permission-independent)

The change feed must work in *every* mode, including when Claude stops asking — so it is driven by the
**hook stream**, not by openDiff. `SessionChangeTracker` (`Weavie.Core.Changes`) snapshots each file's
baseline on PreToolUse and records the new content on PostToolUse, because hooks fire before the permission
check. Edits are recorded whether they were reviewed (default), auto-applied (acceptEdits), or hook-allowed.

- The recorded change feed + the **post-turn review navigator** are the review surface in **every** mode,
  default included — see [turn-review.md](turn-review.md). The embedded `claude` applies its built-in
  Edit/Write edits directly (recorded via the hook stream) rather than calling the blocking `openDiff`, so the
  always-recording tracker is the reliable surface; gating the navigator on the observed mode would hide
  default-mode edits from review entirely. The hosts therefore push the navigator in all modes (they do **not**
  gate it on `ObservedPermissionMode.AutoAppliesEdits`).
- **openDiff** stays wired as an optional blocking per-edit review *if* Claude ever calls it.
  `PermissionModeDiffPresenter` auto-keeps it when the observed mode auto-applies edits (so it never blocks
  redundantly under acceptEdits). `AutoAppliesEdits` now drives only that openDiff auto-keep, not the navigator.

### Native Codex

Codex uses the same `claude.allowAllTools` compatibility setting as Weavie's shared bypass toggle:

- off: `codex.sandbox` and `codex.approvalPolicy` are passed through;
- on: every Codex turn uses `danger-full-access` plus `never`, and any permission request still emitted by
  app-server is accepted immediately without rendering an approval card.

This does not bypass Codex hook trust. Native Codex injects no Weavie lifecycle hooks and does not pass
`--dangerously-bypass-hook-trust`.

Codex app-server can emit `item/started` after a command has already begun mutating disk, so per-item events
cannot provide a reliable baseline. `SessionChangeTracker` instead snapshots the in-scope workspace once at
`turn/started`, continues using native item notifications for immediate locations/status, and reconciles the
workspace at `turn/completed` (an interrupted turn also ends with `turn/completed`, carrying
`status: "interrupted"` — the protocol has no separate interruption notification). This covers shell, MCP,
dynamic-tool, creation, and deletion paths without depending on incomplete `PreToolUse` interception.

**Known blind spot.** The reconciling walk prunes `WorkspacePaths.IgnoredSegments` (`node_modules`, `.git`,
`bin`, `obj`, `dist`, `.vs`, `.idea`, `out`, `target` — the hardcoded noise list the file index and LSP
watcher share), so a shell/MCP mutation under any directory *named* one of those (`bin/rails`, a
hand-authored `dist/`) never reaches turn review. Explicit-path edits (`fileChange` items) are tracked
regardless. The walk also reads every in-scope file's content at `turn/started` and again at the end — a
real cost on large worktrees. A `.gitignore`-aware walk would fix both and is the open follow-up.

## Architecture / placement

```
Weavie.Core/
  Hooks/
    HookRequest.cs            // + PermissionMode (parsed from permission_mode)
    ObservedPermissionMode.cs // the observed-mode mirror (Observe / Current / AutoAppliesEdits)
    HookPolicy.cs             // Decide(request, allowAllTools): allow non-edit PermissionRequest when on
    HookSettings.cs           // the hooks block (tool matcher) written to claude's --settings
  Configuration/
    CoreSettings.cs           // claude.allowAllTools (Bool, Live) — replaces claude.permissionMode
  Mcp/
    PermissionModeDiffPresenter.cs  // openDiff auto-keep keyed on the observed mode
    IdeIntegration.cs               // hook decision reads claude.allowAllTools live
  Changes/                    // SessionChangeTracker + the turn/session feeds (see turn-review.md)
src/Weavie.Win | Mac | Linux/ // each host: construct ObservedPermissionMode, pass it to the presenter,
                              // subscribe it to HookBridge.Observed (AutoAppliesEdits drives the openDiff
                              // auto-keep only; the review push is unconditional)
```

## Open questions / follow-ups

- **Widen the hook matcher (DONE).** The permission gate is the `PermissionRequest` hook with a `*` matcher,
  so `allowAllTools` covers every prompting tool. `PreToolUse`/`PostToolUse` also match `*` — the session
  status needs every tool start/finish (an approved permission prompt is only observable as the gated tool's
  `PostToolUse`) — and change tracking filters to the edit tools by tool name.
- **Per-tool permission.** Grow `claude.allowAllTools` (bool) into a per-tool allow/deny/ask map — the
  fine-grained version of the tool axis. Edits still defer to the mode unless explicitly overridden.
- **UI mode indicator.** Surface the observed edit mode (and the `allowAllTools` state) in the chrome so both
  axes are visible at a glance; optimistically update the displayed mode the instant a Shift+Tab sequence
  passes through the PTY, then confirm on the next hook event (the observe path lags by one tool call).
- **`plan` mode.** Observed and gated correctly (read-only → no auto-apply, no navigator); there is no
  Weavie-side plan behavior yet.
```
