---
name: record-tour
description: Record a .webm video proving the working-tree change set works, by driving the real Weavie app with Playwright via the weavie-tester agent. Use when the user wants visual or end-to-end proof of a change — "record a tour", "make a video of this", "prove it works", "show me it working".
---

# Record Tour

Produce a recorded `.webm` of the current change set working (or failing), plus a per-scenario
pass/fail report. The heavy lifting is delegated to the **weavie-tester** agent; this skill's job is
to hand it a sharp scenario list and the warm-start notes, then relay its evidence.

## Flow

1. **Enumerate scenarios from the diff.** Read the change set and list what a reviewer would want
   proven: the headline behavior, edge/negative paths, and anything nearby the change could break.
   Fold in any scenarios named in `$ARGUMENTS`. Each scenario must be *visible on video* — pick
   demo content accordingly (see gotchas below).
2. **Launch the weavie-tester agent** with: a summary of the change (files + intent), the scenario
   list, and the warm-start notes below verbatim. The agent builds the stack, writes the gitignored
   `src/web/e2e/tour.local.mjs`, records via `corepack pnpm run capture`, and reports per scenario.
3. **Relay the result**: the per-scenario table, the `.webm` path(s) under
   `src/web/e2e/.recordings/`, any functional spec the agent committed to `src/web/e2e/functional/`
   (and why it earns its keep), and any usability findings. A failing clip is a valid, reportable
   result — never let it be retried into a green run.

`tour.local.mjs` and `.recordings/` are gitignored by design; never commit them.

## Warm-start notes (paste into the agent prompt)

- **Skip what's already warm.** Playwright browsers cache in `~/.cache/ms-playwright` — compare
  against the pinned revisions in `node_modules/playwright-core/browsers.json` before running
  `e2e:install`. `node_modules` present ⇒ no extra `pnpm install`. Dotnet builds are incremental.
- **Minimal sequence (from repo root):**
  ```bash
  dotnet build tools/Weavie.FakeClaude        # claude stub (only if the tour scripts the terminal)
  cd src/web
  # write gitignored e2e/tour.local.mjs exporting `async function tour(page)`
  corepack pnpm run capture                   # pnpm install + web build + host build + record
  # → e2e/.recordings/*.webm  (capture builds Weavie.Headless itself, copying fresh dist into wwwroot)
  ```
- **Knobs:** `WEAVIE_CAPTURE_WORKSPACE=<dir>` records against a demo workspace instead of the repo;
  `WEAVIE_CLAUDE_PATH` + `WEAVIE_FAKE_CLAUDE_SCRIPT` + `WEAVIE_CLAUDE_RESUMESESSION=false` stub the
  claude pane with scripted output. Viewport is 1280×800; the default tour waits out the splash and
  forces dark mode — a custom tour should do the same before demonstrating anything.
- **Demo-content gotchas:** the host doesn't serve workspace files over HTTP, so a preview `<img>`
  needs a raster `data:` URI (markdown-it only allows `data:image/(gif|png|jpeg|webp)`). Mermaid
  flowcharts currently render with blank labels (foreignObject stripped by the sanitize pass) —
  use sequence diagrams for legible clips.
- **Durable regressions** go to `src/web/e2e/functional/` per the agent's own bar; run the suite with
  `corepack pnpm exec playwright test --project=headless` from `src/web`.
