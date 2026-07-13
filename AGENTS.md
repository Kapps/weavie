# Weavie

Agentic code editor that weaves Claude Code, terminal sessions, and full code editing into one workflow.

## Concepts

High-level architecture concepts. Each line is the whole idea; follow the link for detail and
load it only when you need it.

- **Claude-facing capability registry** — Weavie embeds Claude Code and exposes its own
  capabilities back to it over the IDE-MCP server, so the user can drive Weavie by talking to
  Claude. Capabilities are *registered* in Core and surfaced as MCP tools: **settings** (being
  built now) and **commands** (named actions, not yet implemented). See
  [docs/concepts/mcp-registry.md](docs/concepts/mcp-registry.md).
- **Hook bridge** — the spawned `claude` runs a standalone relay binary (`Weavie.HookRelay`, co-located with the host
  by the build) as its hooks — PermissionRequest (the permission gate, fires only when a tool would prompt)
  plus PreToolUse/PostToolUse (the change-tracking feed) — via a per-instance `--settings` file. The relay forwards
  each tool call over a current-user-only named pipe (no token) to the in-process `HookBridgeServer`,
  which records every mutating tool call and returns an allow/deny/pass-through
  decision — letting Weavie observe + gate tools without `--dangerously-skip-permissions`. See
  [docs/concepts/hook-bridge.md](docs/concepts/hook-bridge.md).
- **Host core** — every platform host (Win/Mac/Linux/Headless) is a thin shell over one shared
  `HostCore` (`Weavie.Hosting`) that owns the Core graph, the web-message dispatch, and the session
  set; each shell supplies only its native bits through `IHostPlatform` (bridge, UI-thread marshal, PTY
  launcher, optional window/hotkeys/dialogs). **Add host-facing features to the core, not per-OS** — a
  new web message, push, or session behavior goes in `HostCore` so all four hosts get it at once. See
  [docs/specs/host-core-unification.md](docs/specs/host-core-unification.md).
- **Contextual suggestions** — a Core-owned surface for dismissible nudge cards that teach users what
  Weavie can do. Declared once (`SuggestionDefinition`/`CoreSuggestions`), evaluated per-workspace by
  `SuggestionService` (with a bounded, fail-open manifest probe off the hot path), and rendered as cards;
  acting on one runs a command (never spends model tokens until the user clicks). First instance: the
  `workspace.setup` nudge — have Claude configure the repo's worktree setup command + test profile. See
  [docs/concepts/suggestions.md](docs/concepts/suggestions.md).
- **Built-in workspace auto-config** — when a workspace opens with its setup settings unset, Weavie
  detects the language from a curated, hardcoded catalog (TS/C#/Go now; Rust/Python next) and writes
  `worktree.setupCommand` + `test.profile` itself — deterministic, zero tokens, instant. The Claude
  setup flow is demoted to the override / unsupported-language fallback. Supersedes the "no bundled
  presets" stance. See [docs/concepts/workspace-autoconfig.md](docs/concepts/workspace-autoconfig.md).
- **Learn from corrections** — Weavie uniquely sees the user's *edit over the agent's output*, invisible to
  the model's transcript. Each correction is captured as a discrete event **at the moment the user acts** —
  an editor save that lands over an agent hunk, or a review-UI revert — gated to the lines the agent wrote
  (never by scanning the tree). It rings those per-workspace and the palette command `Learn From My
  Corrections` (`weavie.learn.fromCorrections`) prefills the corpus into the primary session's Claude to
  propose `AGENTS.md` rules — Weavie stores the signal, Claude does the reasoning (no classifier in Core). A
  contextual-suggestion card nudges once enough accumulate. See
  [docs/specs/learn-from-corrections.md](docs/specs/learn-from-corrections.md).

## Keyboard-first navigation

Weavie is designed to **encourage keyboard navigation**. Make it as easy as possible for users to
build the habit, and help them discover what's available — an unfamiliar user should be able to learn
the keyboard path for an action just by using the mouse.

- **Every action with a keybinding advertises its shortcut where the user meets it** — a button's
  hover tooltip (e.g. `Accept (Ctrl+Enter)`), a menu item, a palette row. Don't ship a click target
  whose shortcut is invisible.
- **Read the effective binding from the command catalog** (`CommandInfo.keys`, formatted with
  `formatKey`); never hardcode the keys. They're user-overridable in `~/.weavie/keybindings.json`, so
  a hardcoded label goes stale. Unbound commands show just the label.
- **New user-facing actions get a command + default keybinding** (see
  [docs/specs/commands.md](docs/specs/commands.md)), not just an isolated handler — so they're
  reachable from the keyboard, the palette, and Claude alike.

## Process supervision

Every **long-lived child process** Weavie spawns (the embedded `claude` TUI, shell panes, language servers,
the dev server) MUST be launched through `ProcessSupervisor` (`Weavie.Core.Processes`) with an explicit
`RestartPolicy` — do not hand-roll `Process.Start`/PTY lifecycle plus restart logic. The supervisor owns
launch, crash-restart with backoff, the crash-loop breaker, and clean teardown; it takes `start`/`stop`
delegates so it works for both PTY children and `System.Diagnostics.Process`. Transient one-shot helpers
(e.g. the hook relay) are exempt. See [docs/specs/process-supervisor.md](docs/specs/process-supervisor.md).

## Failures & CI

- **A failure is yours once it's real and reproducible — especially any red CI check on your PR.
  Always fix errors and CI failures; never wave one off as "not my problem" or "pre-existing"
  because it's outside your change set.** A green pipeline is the bar for done. If a fix is genuinely
  outside your scope or ambiguous, say so and ask — don't ignore it.
- **Never accept a flaky test and hide it — that's a silent fallback.** A skip, a quarantine, a
  retry loop, a loosened assertion, or "re-ran it and it passed" all bury the defect. When you find
  a flake, root-cause it and fix it.
- **Never install a fake or stub binary into a shared PATH directory** (e.g. a stub `codex` beside
  node in nvm's bin) — it hijacks the real binary for every other session and the user's live app,
  long after your task ends. Keep fakes under `temp/` and wire them in via settings (`codex.path`,
  `TerminalController.ResolveClaudeLaunch`), scoped to your test.

## Custom agents

Four project agents live in `.claude/agents/` — prefer them over reinventing their job inline:

- **`weavie-reviewer`** — reviews a change set for correctness and for the standards below.
  **Invoke it on the result of any non-trivial change before treating the work as done**, and act on
  what it finds. It reviews only your change set, never pre-existing issues (see above).
- **`weavie-architect`** — designs a feature's implementation against Weavie's patterns (Core-first,
  capabilities-as-commands, `ProcessSupervisor`) and returns a blueprint. Use it before building
  anything with real architectural surface.
- **`weavie-tester`** — proves a change works by running the real app, exercising the scenarios a PR
  should cover, and recording a `.webm` that **shows the feature in action** as evidence. **When
  running in a remote (sandbox) environment, invoke it to validate any change before treating the work
  as done** — there it can build the host and drive the full stack; locally it's optional. **Always
  relay the recorded video(s) to the user**: quote each `.webm` path it returns as a **repo-relative
  path** (e.g. `src/web/e2e/.recordings/<clip>.webm`, not an absolute `/home/...` path) so Weavie
  renders it as a clickable, viewable link — one path per line with a one-line label of what it shows.
- **`product-strategist`** — proposes features that fit the product thesis. Use it when deciding
  *what* to build, not *how*.

## Integration testing

Regressions live in **our** code, not the model's. Full-stack tests stub `claude` at the process
seam (`TerminalController.ResolveClaudeLaunch`) so a journey through the whole stack (web → WSS →
HostCore → PTY → hook bridge → MCP → render) is **deterministic** — no test ever runs the real model.
**Transport is a harness parameter, not a duplicated suite**:
run the full functional suite on `headless`, only the transport-sensitive delta on `remote`. See
[docs/specs/integration-testing-strategy.md](docs/specs/integration-testing-strategy.md).

## Code standards

- **Minimize lines of code.** Every line is a liability — to read, maintain, and break. Write the
  least code that does the job, prefer the plainest version of it, and delete more than you add when
  you can.
- **No duplication.** Repeated logic is a defect, not a shortcut — the first time you'd copy
  something, extract the shared part to one place (a helper, a base, a single source of truth).
- **Query the authoritative source; never hardcode a copy of it.** When git, the OS, or a config
  already answers a question — which paths are ignored, what's tracked, where a tool lives — ask it,
  don't restate it in a hand-maintained list that silently drifts (a hardcoded ignored-directory list
  beside a real `.gitignore` is a duplication bug, not a shortcut). Encoding genuinely-new knowledge no
  source holds (e.g. the language→setup catalog) is the exception, not license to reinvent one.
- **Change capture never walks the workspace.** Correction and agent-change tracking must be driven by
  provider-reported or already-tracked paths; never read, snapshot, or enumerate every workspace file.
- **No fallbacks.** Never paper over a hang or failure with a safety-net timeout, a cap, or a default
  that hides it. Don't add one unless explicitly asked — the absence of a fallback is the default.
  When a bound is genuinely required, fail loudly *at the surface that meets the user*: a console log
  or any other dev-only sink is still a silent fallback, because the person hitting the limit never
  sees it (e.g. capping a file index at 20k and only logging it leaves files silently unopenable).
  Surface it where the affected user is, or don't impose the bound.
- **No nullable injected dependencies.** Don't accept `IFoo? = null`. Provide a `Noop`/`Headless`
  implementation and require the real thing.
- **No optional / default-valued parameters.** Banned repo-wide by the `WV0001` analyzer (only
  carve-outs: `CancellationToken` and `Caller*`). Use overloads or the test factory.
- **Enforce, don't suppress.** Strict enforcement is the default; never silence an analyzer or
  warning to make a problem disappear. Ask before any build-config, enforcement, or philosophy call.
- **Run the formatter before calling a change done.** Web/TS: `biome check --write` (Biome,
  `lineWidth: 100`); .NET: `dotnet format`. Both are CI gates — hand-fixing wrap after the fact, or a
  red check, means it wasn't run.

## File size & single responsibility

Any source file that grows past **300 lines** is a signal to stop and ask whether it's doing too much —
and, if so, split it along its seams. Length isn't the violation; *mixed responsibilities* are. A file that
has clearly outgrown one job (e.g. a window/app class that owns bootstrap **and** the web-message bridge
**and** workspace management **and** JSON building) should be broken into focused files — use `partial`
classes (mirroring `WorkspaceWindow` + `WorkspaceWindow.WebBridge.cs`) or extracted collaborators. Prefer
cohesive ~200-line files over one sprawling one. Don't split a file that genuinely does one thing just to
hit a number.

## Comments & prose

- **Minimal comments, each carrying its own weight.** Prefer self-explanatory code over narration;
  write a comment only when it tells the reader something the code can't. Keep it to 1–2 lines (one
  ideal, two max) — anything longer needs an extremely good reason, so push the detail into the code
  or a linked doc. Public APIs are the exception: XML doc comments are required there (CS1591);
  single-line summaries are fine when they stay readable.
- **State the final state, not the path to it.** Never narrate the order of operations that produced
  something ("first X, then Y, now Z"); say only what it *is* now. Applies to comments, commit
  messages, and docs.

## Output conventions

Keep the repo root clean. Do not drop scratch files, findings, or notes in the root.

- **Intermediate / scratch output** — investigation notes, findings, logs, throwaway
  scripts, experiment results, draft analysis → write to `temp/`. This folder is
  gitignored; nothing in it is committed.
- **Specs & designs** — design docs and technical specs you intend to keep →
  write to `docs/specs/` (one file or folder per spec). These are tracked and reviewed.
- **Concepts** — high-level architecture concepts meant to be loaded on demand →
  write to `docs/concepts/` (one file per concept). Keep the AGENTS.md "Concepts" section to a
  one-line summary + link per concept; the detail lives in the doc.
- **Diagrams in docs** — draw diagrams as Mermaid (` ```mermaid ` fenced blocks), never hand-drawn
  ASCII-art. Applies to specs and design docs.

## Debug & instrumentation flags

- **No buried environment variables.** Tracing, diagnostics, logging, and instrumentation toggles
  must be real **settings** (in the settings system, surfaced via the capability registry), **off by
  default** — never hidden env vars a user can't discover or flip. If a flag is worth having, it's
  worth being a first-class setting. One-off, throwaway diagnosis during development is fine, but it
  does not get committed — nothing buried lands in the codebase.
