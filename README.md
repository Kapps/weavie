# weavie

Agentic code editor that weaves Claude Code, terminal sessions, and full code editing into one workflow.

weavie keeps Claude Code as the primary driver — Claude does the bulk of the work, and the terminal
stays front and center. What it adds is hands-on editing: a real editor beside the Claude pane, with
full LSP, autocomplete, and go-to-definition, for the small changes you'd rather make yourself.

It's keyboard-first by design. The mouse is mostly for working through changesets; everything else
stays on the keyboard, right next to the Claude pane and the shells.

The split is Claude drives, you steer. weavie is for making the precise little edits and guiding
Claude, not for hand-writing whole features and not for handing everything to Claude either. That
in-between is the part plain Claude Code doesn't cover. It removes most IDE bloat, designed to be
as minimal UI as possible.

It can also drive itself on request — change settings, run commands, switch themes, open diffs — since
weavie exposes its own features to Claude as MCP tools. For example, ask it to make all colours
in your theme 20% darker or to split your layout into a left-right split. Weavie acts as an MCP server
and allows Claude to drive it so you don't have to learn editor-specific functionality.

## State of things

Early, and not a daily driver yet:

- No releases or installer — build it from source.
- Needs the .NET 10 SDK and an existing Claude Code install.
- Windows is the supported host. The Mac build compiles but is barely exercised, and there's no Linux
  build yet.
- Lots of functionality you'd expect doesn't exist yet... including tabs or sessions.
- Nothing yet around packaging, signing, or shipping an actual binary.

## How it fits together

Two pieces do most of the work:

- A capability registry. weavie's settings and commands are declared once in the core, and they show
  up to the embedded Claude as `mcp__weavie__*` tools on their own — the same declaration also drives
  the keybindings and the command palette. Writeup:
  [docs/concepts/mcp-registry.md](docs/concepts/mcp-registry.md).
- A hook bridge. The embedded claude runs weavie itself as its tool-use hook, over a named pipe only
  the current user can open. That's how weavie sees every edit and command Claude runs — which feeds
  the change log — and gates them (the permission modes) without ever running claude with
  `--dangerously-skip-permissions`. Writeup:
  [docs/concepts/hook-bridge.md](docs/concepts/hook-bridge.md).

## What works

On Windows:

- Claude Code in a terminal pane, plus regular shell panes.
- A Monaco editor with inline diffs to keep or reject for Claude's edits, and the open file and
  selection handed to Claude as context.
- Syntax highlighting for ~200 languages, and language servers for C#, Go, and TypeScript with
  diagnostics in the editor.
- Settings and commands/keybindings, both drivable by talking to Claude, plus a command palette and
  global hotkeys.
- Permission modes and a live log of everything Claude touches.
- A custom title bar and a welcome / recent-folders screen.

## What's where

- `src/Weavie.Core/` — platform-agnostic core: MCP servers, capability registry, settings, hooks, the
  LSP bridge, theming, process supervision.
- `src/Weavie.Win/` — Windows host (WinForms + WebView2), the supported one.
- `src/Weavie.Mac/` — macOS host (AppKit + WKWebView), builds but barely tested.
- `src/web/` — the UI both hosts share (SolidJS + TypeScript, Monaco, xterm), built with Vite.
- `tests/` — core unit tests.
- `tools/` — throwaway harnesses for poking at the LSP and IDE-MCP bridges.
- `docs/` — `concepts/` for the ideas, `specs/` for the designs.

## Building it

Needs a preview .NET 10 SDK (global.json rolls forward and allows prerelease), Node for the UI, and
Claude Code (`claude`) on the PATH since weavie spawns it. The UI uses pnpm, run through Corepack
(bundled with Node) — the `corepack pnpm` commands below need no separate install; `corepack enable`
once puts a bare `pnpm` on the PATH if you prefer it. On Windows that also means Windows 10 1809+ and
the WebView2 runtime (already there on Windows 11). For LSP, put `csharp-ls`, `gopls`, or `tsgo` on
the PATH.

Windows:

```sh
cd src/web && corepack pnpm install && cd ../..
dotnet build src/Weavie.Win/Weavie.Win.csproj -c Release
```

A release build runs `pnpm run build` and drops the UI next to the exe, so the build is runnable as-is.
For development, run it in Debug (`dotnet run --project src/Weavie.Win/Weavie.Win.csproj`) — the host
launches the Vite dev server itself, so the UI hot-reloads with no further npm steps.

macOS (experimental):

```sh
cd src/web && corepack pnpm install && corepack pnpm run build && cd ../..
dotnet build src/Weavie.Mac/Weavie.Mac.csproj -c Release
```

## License

MIT — see [LICENSE](LICENSE).
