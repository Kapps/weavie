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
