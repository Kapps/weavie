# Warm language clients across session switches

## Problem

Switching between sessions cold-started language intelligence every time. Each session has its own
worktree + bridge slot, and the frontend tore **every** language client down on each `lsp-config`
push (session switch *and* every reconnect/refresh `ready`), then reconnected on fresh channels. For a
heavy server like `csharp-ls`/Roslyn that means a multi-second workspace re-index on every switch-back —
a papercut the more you use sessions. The same churn also drove the duplicate-code-action races fixed in
#290 (tear-down + rebuild on every remote reconnect opened windows for two live clients at once).

## Approach

Keep clients **warm** in a pool instead of tearing them down on switch. The enabling facts (all verified
against the code, not assumed):

- The **host already keeps each session's `LspController` + its `csharp-ls` alive until the session is
  unloaded** — `SwitchToSlot` never touches LSP; only `UnloadSlotAsync`/process-shutdown dispose it. So
  no host change is needed.
- The host **routes LSP strictly by slot** (`SessionForSlot`), with no active-session gating, so a
  backgrounded same-backend session's server is serviced in both directions.
- A **bridge drop does not tear down channels** host-side, and channel ids stay valid across reconnect —
  so reusing a warm client over a reconnected socket is safe (it also removes the old reconnect churn).
- Monaco's provider registry is **global per language**: two live `csharp` clients both feed the "More
  Actions" menu. So each client's `documentSelector` is scoped to its worktree with a `pattern`
  (`<root>/**`), and only the client that owns the active file's worktree ever answers.

## Design

Two modules:

- **`language-client-pool.ts`** — the warm pool, keyed by `(backendId, slot, serverId)`: one live
  `MonacoLanguageClient` per language *per worktree*. Owns the per-client connection lifecycle (channel,
  supervised reconnect, orphan-prevention), fenced per-key by pool identity (a switch/prune/newer attempt
  replaces the entry and supersedes stale work — the #290 fix, generalized off the removed global
  `generation`). Exposes `ensureClient` (idempotent reuse-or-create), `pruneForeignBackend`,
  `pruneUnloaded`.
- **`lsp-client.ts`** — the manager: maps each open model to **its own worktree's** client by path
  (a retained `slot → config` map + longest-prefix match), so a backgrounded worktree stays served
  correctly regardless of whether its editor models persist across a switch. Wires the model hooks, the
  `session-list` unload signal, and `rebindLanguageServices`.

### Lifecycle

| Event | Behavior |
|---|---|
| First open in active session | Lazily `ensureClient` for the model's worktree. |
| Switch away | Client stays **warm** (not disposed, no `lsp-stop`). |
| Switch back | `ensureClient` finds the warm client → no reconnect, no re-index. |
| Reconnect / refresh `ready` | Same — warm client reused over the same (still-valid) channel. |
| Session **unload / delete** | `session-list` shows the slot unloaded → `pruneUnloaded` disposes it (+ `lsp-stop`), so a late frame can't misroute into the active session. |
| **Backend** switch | `pruneForeignBackend` disposes clients not on the active backend (their transport is stranded). |
| Backgrounded server crash | Reconnects only while a model under its worktree is open; otherwise dropped and re-created lazily on switch-back. |

```mermaid
sequenceDiagram
  participant Web as web (lsp-client + pool)
  participant Host as HostCore (per-slot LspController)
  Note over Web,Host: switch A→B (same backend)
  Host->>Web: lsp-config(B)
  Web->>Web: record B; prune foreign=∅; A stays warm; ensure B
  Note over Web,Host: switch B→A
  Host->>Web: lsp-config(A)
  Web->>Web: A model reopens → warm A client answers (no re-init)
  Note over Web,Host: unload B
  Host->>Web: session-list (B not loaded)
  Web->>Web: pruneUnloaded → dispose B + lsp-stop
```

## Scope / non-goals

- **Same-backend only.** Cross-backend (a remote session backgrounded behind a local one) can't stay warm
  — its bridge transport is gated to the active backend — so those are pruned on backend switch.
- **No resource cap.** N warm sessions = N live servers, but that already matched the host lifecycle
  (each loaded slot holds its server until unload); warm-keeping the page clients adds no server processes,
  only defers their teardown to unload. A cap would be a silent fallback and is deliberately not added; if
  ever wanted it must be a visible setting, not a buried LRU.

## Testing

`lsp-client.test.ts` drives the manager + pool with monaco / the language client / the bridge transport
stubbed (real `fs-path`): warm-across-switch, reuse-on-switch-back, per-worktree selector scoping (no
double menu), teardown-on-unload, cross-backend teardown, and the #290 stale-reconnect fence. Runtime
provider scoping in real Monaco (that a worktree's client answers for its own files and only those) is
proven end-to-end by the tester, since importing vscode's glob matcher into the node unit env is not
representative.
