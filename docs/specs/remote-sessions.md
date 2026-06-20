# Remote sessions

Status: **in progress** — the runner (scenario 1, worktree mode) is built; container mode and the
local-shell session picker are follow-ups.

Weavie can run a session's *backend* — the embedded `claude`, the shell PTY, the file provider, the
LSP servers, change tracking — on a **remote host**, while the editor and terminal *rendering* stay
local. This spec describes how.

## The one idea

Everything that must sit next to `claude` already sits behind a single seam: `IHostBridge`
(`MessageReceived` / `PostToWeb(json)`). `Weavie.Headless` already runs the *entire* `Weavie.Core`
session graph behind that seam over a WebSocket (`/weavie-bridge`). So a remote backend is just
**`Weavie.Headless`, running on another machine, with the bridge crossing the network** — nothing in
the graph has to move or change, because the PTYs, the IDE-MCP loopback servers, the hook pipe, the
LSP servers, and local disk are all co-located *with the backend*, which is exactly where they need
to be. The only thing on the wire is the bridge JSON stream.

```
Local (rendering only)              Remote host (the whole graph = Weavie.Headless)
  Monaco / xterm.js / LSP client      FileProvider · PTYs(claude+shell) · LSP servers · hooks · disk
        |                                   |
        +------ bridge JSON over wss --------+   (the only thing that crosses the network)
```

The corollary: the loopback/named-pipe pieces (IDE-MCP, hook bridge, LSP) are **not** remoting
problems. They are local *to the backend*. The one genuinely new wiring item is tunnelling the LSP
client↔server WS over the bridge instead of loopback (see [Deferred](#deferred)).

## What runs where

| Layer | Runs | Reaches the other side via |
| --- | --- | --- |
| Rendering — Monaco, xterm.js, layout, palette, LSP **client** | Always local (WebView / browser) | bridge JSON only |
| Backend graph — `FileProviderService`, PTYs, LSP **servers**, IDE-MCP, hooks, change tracking | Wherever the backend is; moves as one unit (`Weavie.Headless`) | services bridge messages against disk/processes local to it |

The editor is **not** relocated. Monaco renders locally and never touches a disk — it reads/writes
through `fs-read` / `fs-write` / `fs-stat` bridge messages serviced by `FileProviderService` *in the
backend graph*, against the backend's own disk. When the backend is remote, Monaco is already editing
remote files. The `IFileSystem` abstraction is **not** the remoting mechanism (the backend just uses
`LocalFileSystem` against the host it runs on); the bridge message protocol is.

LSP cannot be inverted (local server, remote files): a language server reads most of a project —
closed files, `tsconfig`/`go.mod`/`Cargo.toml`, dependency trees, its own indexing/watching —
**directly from disk by path**, and there is no protocol path for it to fetch arbitrary file contents
from the client. So the server runs remote, next to the files; only the protocol messages cross to a
local client. (This is the VS Code Remote / JetBrains Gateway model.)

## The runner — Option C

The long-lived thing on the box is **not** a session backend. It is a small **manager** (a factory +
supervisor). Each session is its **own** `Weavie.Headless` process with its **own** `{ url, token }`.

```
RUNNER daemon  (long-lived, one auth'd control endpoint)
  ├─ control plane:  create / list / destroy a session
  └─ per session:    a supervised Weavie.Headless worker, addressable at its own URL
       session A → headless @ host:PORT_A  (worktree A)   ── frontend connects directly ──┐
       session B → headless @ host:PORT_B  (worktree B)   ── frontend connects directly ──┤→ rendering
```

Why this shape (vs. one long-lived headless holding all sessions):

- **Isolation** — each session's `claude` + PTYs run in their own process; one wedged session can't
  take the others, or the box, down. Clean teardown = kill the worker.
- **Worktree-vs-container collapses into the spawn delegate.** "Worktree session" = spawn a headless
  process rooted at a fresh `git worktree`. "Isolated session" = spawn a container running headless.
  Same manager, two `start`/`stop` delegates — which is precisely `ProcessSupervisor`'s shape, so the
  runner supervises its workers the same way `TerminalController` supervises PTYs.
- **No reverse proxy, no registration.** Each worker is self-describing via its URL; the manager
  hands back `{ url, token }` and gets out of the data path. The frontend connects **directly** to the
  worker (we assume the client can always open a connection to the box). The manager never relays
  bridge traffic.

### Control plane

A small auth'd HTTP surface on the runner (token via `Authorization: Bearer <t>` or `?token=<t>`,
matching the runner token). All it does is mint backends:

| Method | Route | Does |
| --- | --- | --- |
| `GET` | `/sessions` | List live sessions: `{ id, branch, status, url }`. |
| `POST` | `/sessions` | Create. Body `{ branch?, base? }`. `git worktree add` + spawn a headless worker on a fresh port with a per-session token. Returns `{ id, branch, status, url }`. |
| `DELETE` | `/sessions/{id}` | Stop the worker (and remove its worktree). |
| `GET` | `/` | A minimal picker page (lists sessions, "New session", links to each). |

`status` is derived from the worker's `ProcessSupervisor` state (`starting` / `running` / `backing-off`
/ `failed`). The session `url` host is derived from the control request's `Host` header (so reaching
the runner at `box:9000` yields session URLs at `box:<port>`) — no public-host config needed.

### Per-session worker

`POST /sessions` →

1. `WorktreeManager.CreateAsync(branch, baseRef)` — a real `git worktree` under
   `~/.weavie/workspaces/<id>/worktrees/`, recorded in the registry (reconciled against
   `git worktree list`, so nothing leaks). `base` `"head"` → `HEAD`; otherwise the literal ref.
2. Allocate a free port and a per-session token.
3. `ProcessSupervisor` (policy `OnFailure`) wrapping
   `Weavie.Headless --port <p> --bind <host> --workspace <worktree> --token <t>`.

The worker serves the full Weavie web UI at its URL **and** the bridge at `/weavie-bridge`. The bridge
upgrade **requires** the token (`?token=`); the page is reached at `…/?token=<t>` and `bridge.ts`
carries that token onto the `auto` bridge URL.

Always-new-session for now: there is no "main session" concept; every create makes a fresh worktree
session (simplification — a default/attach-existing session can come later).

## The three scenarios — one primitive, different creators

The shared currency everywhere is `{ url, token }` + the existing bridge. Only *who mints it* differs:

1. **Self-hosted (AWS dev box) — primary.** Install the runner on the box. New Session → the runner
   `git worktree add`s and spawns a worker (worktree mode). Later: container mode for isolation, same
   interface, connect just takes longer. Testable entirely on loopback (run the runner as a separate
   process, connect to `127.0.0.1`) — needs no cloud.
2. **Claude Code Cloud Agent.** The container *is* the isolation boundary, so there is no
   runner-as-a-service to build — the **startup script is the creator**: it boots a headless and emits
   `{ url, token }` (a URL with an embedded token), which the user pastes into Weavie. Same primitive,
   creation done once out-of-band.
3. **Developing Weavie.** Same as (2), but the headless serves the web assets too, so the dev just
   points a **browser** at the worker URL and sees both web and backend changes from the branch.

## Auth & transport (first cut → hardening)

- **First cut (built):** a runner token gates the control plane; a per-session token gates each
  worker's bridge WS. Tokens are random hex, minted by the runner.
- **Hardening (deferred):** TLS on both the control plane and the worker bridges; short-lived
  per-session tokens minted from the runner token; gating the worker *page* (not just the bridge) and
  static assets via a cookie set from the `?token=` landing.

## Deferred

- **LSP over the bridge.** Today the LSP bridge is a separate loopback WS not surfaced to a remote
  browser; tunnel it over (or alongside) the authed bridge so remote editing gets language features.
- **Container mode.** A second pair of `ProcessSupervisor` `start`/`stop` delegates (run a container
  running headless instead of a local process); the control plane and frontend are unchanged.
- **Local-shell session picker.** "New Session → Local | Remote(runner)" in the native shells: a
  runner connection the frontend attaches to, listing/creating sessions and dialing the returned URL.
- **Reattach / durability.** The runner already knows its live workers, so list-and-reattach after a
  dropped frontend is natural; worker bridges already survive a dropped page (refresh re-attaches).
