# Remote sessions

Status: **in progress** — the runner (scenario 1) provisions one multi-session backend per workspace;
New Session creates worktree sessions on the remote box. Container isolation and the native-shell
"connect to runner" UI are follow-ups.

> **Updated after the host-core unification** ([host-core-unification.md](host-core-unification.md)):
> the session model (`new-session`, worktrees, the rail) moved out of `Weavie.Win` into shared
> `Weavie.Hosting/HostCore`, and **every host drives it — including Headless via `HeadlessPlatform`**.
> Two consequences: (1) a single `Weavie.Headless` is now *multi-session* — connect to one and New
> Session creates worktrees inside it, exactly like local; (2) any remote-session wiring lands once in
> shared code and is runnable + capturable via Headless. This collapsed the earlier "spawn a process
> per session" design (Option C): per-session **processes on the same box** buy little once HostCore
> isolates each session's claude/PTY/MCP in-process, so the runner now provisions **one multi-session
> backend per workspace**, and real isolation is an opt-in **container** tier, not a process tier.

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

## The runner

The long-lived thing on the box is **not** a session backend itself. It is a small **manager** that
provisions, auths, and supervises **one multi-session `Weavie.Headless` worker per workspace** and hands
the client that worker's `{ url, token }`. Worktree sessions are created *inside* the worker by the shared
`HostCore` (New Session → `new-session` → a `git worktree`), exactly like local — the runner does not
manage individual sessions.

```
RUNNER daemon  (long-lived, one auth'd control endpoint)
  ├─ control plane:  ensure / connect to the workspace backend
  └─ per workspace:  a supervised, multi-session Weavie.Headless worker @ host:PORT (its own token)
                       ↳ New Session inside it → git worktree on the box (HostCore)   → rendering
```

Why this shape (and not a process per session):

- **HostCore already isolates the expensive, crash-prone per-session parts** — each `HostSession` owns
  its own claude PTY + supervisor + IDE-MCP + hook bridge, in-process. A separate OS process per session
  on the *same box* adds management cost for little extra isolation (same filesystem, OS, creds, resource
  pool). So the worktree case is one multi-session worker.
- **Real isolation is a container tier, not a process tier.** When you need a security boundary /
  resource caps / reproducible env, wrap the worker in a **container** (per workspace, or per session if
  truly needed). That is the spawn-delegate axis — `ProcessSupervisor` with a container `start`/`stop`
  instead of a process `start`/`stop` — and it is the deferred opt-in.
- **No reverse proxy, no registration.** The worker is self-describing via its URL; the manager hands
  back `{ url, token }` and gets out of the data path. The frontend connects **directly** to the worker
  (we assume the client can always open a connection to the box). The manager never relays bridge traffic.

### Control plane

A small auth'd HTTP surface on the runner (token via `Authorization: Bearer <t>` or `?token=<t>`,
matching the runner token):

| Method | Route | Does |
| --- | --- | --- |
| `GET` | `/backend` | Ensure the workspace's worker is running; return `{ url, status, workspace }`. |
| `GET` | `/` | A minimal landing page: ensure the backend, offer one "Open Weavie" link into it. |

`status` is derived from the worker's `ProcessSupervisor` state (`starting` / `running` / `failed` /
`stopped`). The backend `url` host is derived from the control request's `Host` header (so reaching the
runner at `box:9000` yields a backend URL at `box:<port>`) — no public-host config needed.

### The worker

`GET /backend` (and runner startup) ensures one worker:

1. Allocate a free port and a per-worker token.
2. `ProcessSupervisor` (policy `OnFailure`) wrapping
   `Weavie.Headless --port <p> --bind <host> --workspace <repo-root> --token <t>`.

The worker serves the full Weavie web UI at its URL **and** the bridge at `/weavie-bridge`. The bridge
upgrade **requires** the token (`?token=`); the page is reached at `…/?token=<t>` and `bridge.ts` carries
that token onto the `auto` bridge URL. Inside the app, **New Session creates a worktree on the box** via
the shared `HostCore` flow (recorded in the worktree registry, reconciled against `git worktree list`, so
nothing leaks) — no runner involvement per session.

## The three scenarios — one primitive, different creators

The shared currency everywhere is `{ url, token }` + the existing bridge. Only *who mints it* differs:

1. **Self-hosted (AWS dev box) — primary.** Install the runner on the box; it runs one multi-session
   worker for the workspace. Connect to it and New Session creates worktrees on the box, like local.
   Later: container isolation, same interface, connect just takes longer. Testable entirely on loopback
   (run the runner as a separate process, connect to `127.0.0.1`) — needs no cloud.
2. **Claude Code Cloud Agent.** The container *is* the isolation boundary, so there is no
   runner-as-a-service to build — the **startup script is the creator**: it boots a headless and emits
   `{ url, token }` (a URL with an embedded token), which the user pastes into Weavie. Same primitive,
   creation done once out-of-band.
3. **Developing Weavie.** Same as (2), but the headless serves the web assets too, so the dev just
   points a **browser** at the worker URL and sees both web and backend changes from the branch.

## Auth & transport (first cut → hardening)

- **First cut (built):** a runner token gates the control plane; a per-worker token gates the worker's
  bridge WS. Tokens are random hex, minted by the runner.
- **Hardening (deferred):** TLS on both the control plane and the worker bridge; short-lived worker
  tokens minted from the runner token; gating the worker *page* (not just the bridge) and static assets
  via a cookie set from the `?token=` landing.

## Deferred

- **LSP over the bridge.** Today the LSP bridge is a separate loopback WS not surfaced to a remote
  browser; tunnel it over (or alongside) the authed bridge so remote editing gets language features.
- **Container isolation.** A second pair of `ProcessSupervisor` `start`/`stop` delegates (run a
  container running headless instead of a local process); the control plane and frontend are unchanged.
- **Native-shell "connect to runner."** Today a remote backend is reached by opening the worker URL in
  a browser (the whole frontend binds to it — works now). The richer product step is the native shell
  binding a session to a remote backend so **local and remote sessions coexist in one rail**; because
  the session model is now shared in `HostCore`, this lands once for all shells.
- **Multiple workspaces per runner.** Today the runner serves one configured workspace; keying workers
  by workspace (and a small registry) generalizes it.
- **Reattach / durability.** The long-lived worker survives a dropped frontend (its bridge re-attaches
  on refresh); a reconnect just re-opens the saved URL.
