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

## Process supervision

Every **long-lived child process** Weavie spawns (the embedded `claude` TUI, shell panes, language servers,
the dev server) MUST be launched through `ProcessSupervisor` (`Weavie.Core.Processes`) with an explicit
`RestartPolicy` — do not hand-roll `Process.Start`/PTY lifecycle plus restart logic. The supervisor owns
launch, crash-restart with backoff, the crash-loop breaker, and clean teardown; it takes `start`/`stop`
delegates so it works for both PTY children and `System.Diagnostics.Process`. Transient one-shot helpers
(e.g. the hook relay) are exempt. See [docs/specs/process-supervisor.md](docs/specs/process-supervisor.md).

## Shared branch / parallel agents

Multiple agents may work this branch and working tree at the same time. Files can change under you
mid-task, and a build or test can fail on code you didn't touch (another agent's half-saved work).

- Treat failures **outside your own change set** as someone else's in-progress work: don't fix,
  revert, or investigate them, and don't retry in a tight loop. Wait, then re-run.
- Only act on failures in files you actually changed.

## Output conventions

Keep the repo root clean. Do not drop scratch files, findings, or notes in the root.

- **Intermediate / scratch output** — investigation notes, findings, logs, throwaway
  scripts, experiment results, draft analysis → write to `temp/`. This folder is
  gitignored; nothing in it is committed.
- **Specs & designs** — design docs and technical specs you intend to keep →
  write to `docs/specs/` (one file or folder per spec). These are tracked and reviewed.
- **Concepts** — high-level architecture concepts meant to be loaded on demand →
  write to `docs/concepts/` (one file per concept). Keep the CLAUDE.md "Concepts" section to a
  one-line summary + link per concept; the detail lives in the doc.
- **Diagrams in docs** — draw diagrams as Mermaid (` ```mermaid ` fenced blocks), never hand-drawn
  ASCII-art. Applies to specs and design docs.

## Debug & instrumentation flags

- **No buried environment variables.** Tracing, diagnostics, logging, and instrumentation toggles
  must be real **settings** (in the settings system, surfaced via the capability registry), **off by
  default** — never hidden env vars a user can't discover or flip. If a flag is worth having, it's
  worth being a first-class setting. One-off, throwaway diagnosis during development is fine, but it
  does not get committed — nothing buried lands in the codebase.
