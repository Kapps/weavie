# Hook bridge

Weavie gates and records the embedded `claude`'s tool calls **without** putting it in
`bypassPermissions` / `--dangerously-skip-permissions` (which would suppress `openDiff` and the change
feed). Claude always runs in its own `default` mode; Weavie's policy lives on our side, in two cooperating
intercepts:

1. The **openDiff presenter policy** (`PermissionModeDiffPresenter`) — handles the *edit* path when Claude
   asks (`default` → blocking Keep/Reject; `acceptEdits` → auto-keep).
2. The **hook bridge** (this doc) — sees *every* mutating tool call (Bash + edits), is the **change-recording
   stream**, and is the seam where a Weavie-side "bypass" returns `allow` per tool.

The hook bridge also returns, on each landed edit (`PostToolUse` for `Edit`/`Write`/`MultiEdit`), a top-level
`systemMessage` carrying a workspace-relative `path:line` of the first line that edit changed. Claude prints
it in the TUI, and the terminal pane already turns `path:line` tokens into Monaco reveals
(`TerminalView.tsx`), so the user can click straight to the edit. The line is computed from the per-edit
pre-state vs. post-edit content held by `SessionChangeTracker` (`EditLocationFor`), so it pinpoints *this*
edit even on the 2nd+ edit of a file within a turn.

## How it's wired

Claude Code `command` hooks run an arbitrary program per tool call: the event JSON arrives on the hook's
**stdin**, and a JSON decision on its **stdout** controls the tool (`permissionDecision`: `allow`/`deny`/`ask`;
empty = no opinion → normal flow). We deliver a hooks block to *our* claude only, via a per-instance
`--settings` file (additive merge — the user's own hooks still fire; scope comes from the child's argv, so a
claude the user launches by hand is unaffected).

The hook program is **the Weavie host itself** run as `--hook-relay` (like `--pty-smoke`) — no separate binary
to ship, and the host knows its own path (`Environment.ProcessPath`). The relay is transient (one process per
tool call) and **fails open**: any error exits 0 with empty stdout, so a hiccup never blocks Claude. (At a few
tool-calls/min the ~100 ms cold start is invisible; if it ever matters, the relay is a drop-in NativeAOT
binary — the pipe protocol is unchanged.)

```mermaid
sequenceDiagram
    participant C as claude (default mode)
    participant R as relay (host --hook-relay)
    participant S as HookBridgeServer (in-process)
    participant W as Weavie UI / change feed
    C->>R: spawn hook, tool event JSON on stdin
    R->>S: connect pipe (WEAVIE_HOOK_PIPE), framed request
    S->>W: Observed(HookRequest) — record the change
    S->>R: framed decision (empty = pass-through; PostToolUse edit = systemMessage path:line)
    R->>C: decision on stdout (or nothing)
    C->>C: proceed / openDiff / terminal prompt; print clickable path:line
```

## Security model

The channel is a **current-user-only named pipe** (`PipeOptions.CurrentUserOnly`), **not** a loopback TCP port
with a bearer token:

- A **web page** (the CVE-2025-52882 vector that forces the MCP servers to carry a token) **cannot open a pipe
  at all** — the threat is gone structurally, not by a secret.
- **Another OS user** is blocked by the pipe ACL.
- That leaves only **same-user processes**, which already have full access to the existing MCP servers (lock
  file + `--mcp-config` both sit on disk), so the bridge adds no new escalation surface.

Two invariants keep it safe:

- **No shared MCP token.** The dangerous token is the IDE/registry one (`setSetting claude.path=…` → RCE). The
  hook channel never reuses it; the pipe name is injected via env (`WEAVIE_HOOK_PIPE`), disk-less and
  non-secret (auth is the ACL).
- **The endpoint is advisory/powerless.** It records + decides; it never executes a tool. Any destructive UI
  action (revert/apply in the change view) must operate on real file state, never on content an event handed
  us — closing the "spoofed diff → user clicks apply" path.

## Code map (`Weavie.Core/Hooks/`)

- `HookProtocol` — pipe name (`weavie-hook-<port>`), env var, length-prefixed framing.
- `HookRequest` — parses the stdin JSON (event, tool, raw `tool_input`, session, cwd).
- `HookDecision` / `HookPolicy` — the verdict + its stdout JSON serialization (`hookSpecificOutput` permission
  block and/or top-level `systemMessage`); `Decide` is the gate seam (today always `PassThrough`;
  `bypassPermissions` will return `Allow`). `IdeIntegration` attaches the edit jump-link `systemMessage`.
- `HookBridgeServer` — the in-process pipe listener; raises `Observed`, replies with the decision.
- `HookRelayClient` — the `--hook-relay` side: stdin → pipe → stdout, fail-open.
- `HookSettings` — builds the `--settings` hooks JSON.
- Wiring: `IdeIntegration` owns the server + `WriteSettingsFile()` + `WEAVIE_HOOK_PIPE`; the hosts append
  `--settings` and log/consume the `Observed` feed.
