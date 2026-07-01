# weavie

Agentic code editor that weaves Claude Code, terminal sessions, and full code editing into one workflow.

![weavie — Claude Code on the left, a full editor on the right](docs/assets/weavie.png)

weavie keeps Claude Code as the primary driver — Claude does the bulk of the work, and the terminal
stays front and center. What it adds is hands-on editing: a real editor beside the Claude pane, with
full LSP, autocomplete, and go-to-definition, for the small changes you'd rather make yourself.

It's keyboard-first by design. The mouse is mostly for working through changesets; everything else
stays on the keyboard, right next to the Claude pane and the shells.

The split is Claude drives, you steer. weavie is for making the precise little edits and guiding
Claude, not for hand-writing whole features and not for handing everything to Claude either. That
in-between is the part plain Claude Code doesn't cover. It removes most IDE bloat, designed to be
as minimal UI as possible.

It can also drive itself on request — change settings, run commands, switch themes, open diffs, spin
up sessions — since weavie exposes its own features to Claude as MCP tools. For example, ask it to
make all colours in your theme 20% darker or to split your layout into a left-right split. Weavie acts
as an MCP server and allows Claude to drive it so you don't have to learn editor-specific functionality.

## State of things

Coming together, but still early — not a daily driver yet:

- No tagged release or installer yet — build it from source. (The release workflow does publish
  self-contained builds for all three OSes on a tag, so the plumbing is there.)
- Needs the .NET 10 SDK and an existing Claude Code install.
- Windows, macOS, and Linux all build and run now — every push builds all three in CI. Windows is the
  most exercised; the Mac and Linux hosts are newer and less worn-in.
- Sessions exist — each is its own git worktree + branch, switchable from the rail, including remote
  sessions running on another box. That's a recent addition and still settling.
- Lots of functionality you'd expect is still missing or rough around the edges.
- Nothing yet around signing or shipping a polished, installable binary.

## How it fits together

A few pieces do most of the work:

- A capability registry. weavie's settings and commands are declared once in the core, and they show
  up to the embedded Claude as `mcp__weavie__*` tools on their own — the same declaration also drives
  the keybindings and the command palette. Writeup:
  [docs/concepts/mcp-registry.md](docs/concepts/mcp-registry.md).
- A hook bridge. The embedded claude runs weavie itself as its tool-use hook, over a named pipe only
  the current user can open. That's how weavie sees every edit and command Claude runs — which feeds
  the change log — and gates them (the permission modes) without ever running claude with
  `--dangerously-skip-permissions`. Writeup:
  [docs/concepts/hook-bridge.md](docs/concepts/hook-bridge.md).
- A shared host core. Every platform host — Windows, macOS, Linux, and a headless server — is a thin
  shell over one `HostCore` that owns the Core graph, the web-message dispatch, and the session set.
  Host-facing features go in the core once and all four hosts get them. Writeup:
  [docs/specs/host-core-unification.md](docs/specs/host-core-unification.md).

## What works

- Claude Code in a terminal pane, plus regular shell panes with copy/paste and URL-open wired through
  to the host.
- A Monaco editor with tabs, inline diffs to keep or reject for Claude's edits, a two-level changeset
  review, and the open file and selection handed to Claude as context.
- Syntax highlighting for ~200 languages, and language servers for C#, Go, and TypeScript with
  diagnostics, hover, completion, rename, and go-to-definition in the editor.
- Multiple sessions, each a git worktree + branch, switchable from a session rail — including remote
  sessions hosted on another machine and surfaced in the same rail via the headless host.
- Settings, commands, and keybindings, all drivable by talking to Claude, plus a command palette and
  global hotkeys.
- Theming: install VS Code themes from Open VSX, separate light/dark themes with a system-following
  mode, and live colour overrides — all re-themed in place across Monaco, xterm, and the chrome.
- Permission modes and a live log of everything Claude touches.
- A custom title bar and a welcome / recent-folders screen.

## What's where

- `src/Weavie.Core/` — platform-agnostic core: MCP servers, capability registry, settings, hooks, the
  LSP bridge, theming, sessions, workspace indexing, process supervision.
- `src/Weavie.Hosting/` — the shared `HostCore` every shell drives: session set, web-message dispatch,
  terminals, file opener, diff presenter.
- `src/Weavie.Win/` — Windows host (WinForms + WebView2), the most exercised one.
- `src/Weavie.Mac/` — macOS host (AppKit + WKWebView).
- `src/Weavie.Linux/` — Linux host (GTK + WebKit2GTK).
- `src/Weavie.Headless/` — headless host: serves the UI over HTTP/WebSocket so a browser on another box
  can connect to a session, and powers the visual capture and e2e tests.
- `src/Weavie.Runner/` — remote-session manager: spawns headless workers, one per session.
- `src/Weavie.HookRelay/` — the standalone relay claude runs as its hooks, forwarding to the bridge.
- `src/web/` — the UI every host shares (SolidJS + TypeScript, Monaco, xterm), built with Vite.
- `tests/` — core unit tests.
- `tools/` — throwaway harnesses for poking at the LSP and IDE-MCP bridges.
- `docs/` — `concepts/` for the ideas, `specs/` for the designs.

## Building it

Needs a preview .NET 10 SDK (global.json rolls forward and allows prerelease), Node for the UI, and
Claude Code (`claude`) on the PATH since weavie spawns it. The UI uses pnpm, run through Corepack
(bundled with Node) — the `corepack pnpm` commands below need no separate install; `corepack enable`
once puts a bare `pnpm` on the PATH if you prefer it. For LSP, put `csharp-ls`, `gopls`, or `tsgo` on
the PATH.

Build the UI once, then the host for your platform:

```sh
cd src/web && corepack pnpm install && cd ../..
# Windows  (needs Windows 10 1809+ and the WebView2 runtime — already there on Windows 11)
dotnet build src/Weavie.Win/Weavie.Win.csproj -c Release
# macOS    (needs the macOS workload: dotnet workload restore src/Weavie.Mac/Weavie.Mac.csproj)
dotnet build src/Weavie.Mac/Weavie.Mac.csproj -c Release
# Linux    (needs GTK 3 + WebKit2GTK)
dotnet build src/Weavie.Linux/Weavie.Linux.csproj -c Release
```

A Release build runs `pnpm run build` and drops the UI next to the exe, so the build is runnable as-is.
For development, run it in Debug (e.g. `dotnet run --project src/Weavie.Win/Weavie.Win.csproj`) — the
host launches the Vite dev server itself, so the UI hot-reloads with no further npm steps.

## License

MIT — see [LICENSE](LICENSE).
