# Weavie

Agentic code editor that weaves Claude Code, terminal sessions, and full code editing into one workflow.

## Output conventions

Keep the repo root clean. Do not drop scratch files, findings, or notes in the root.

- **Intermediate / scratch output** — investigation notes, findings, logs, throwaway
  scripts, experiment results, draft analysis → write to `temp/`. This folder is
  gitignored; nothing in it is committed.
- **Specs & designs** — design docs and technical specs you intend to keep →
  write to `docs/specs/` (one file or folder per spec). These are tracked and reviewed.
