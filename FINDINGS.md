# weavie First Prototype — Build Findings

> Autonomous overnight build of the weavie First Prototype (a latency-and-feel gate that
> wraps the real interactive Claude Code TUI). Branch: `overnight/first-prototype`.
> This log is honest: a fail or partial is a valid finding. Updated incrementally.

Started: 2026-06-15 (overnight). Machine: Apple Silicon, macOS 25.3.0, 120Hz display.
Toolchain verified: dotnet 10.0.301, node v25.6.0, npm 11.8.0, claude 2.1.177, Xcode + `macos` workload, `ANTHROPIC_API_KEY` UNSET (correct — interactive billing).

---

## Status at a glance

| Step | Item | Status |
|---|---|---|
| 0 | Branch, build gates, CPM, interface seams, example-flow T1 test | ✅ Done |
| 1 | Monaco "just type" + rigorous keypress→paint latency harness | ⏳ In progress |
| 2 | xterm.js + WebGL + PTY → interactive `claude`; verify subscription billing | ⏳ Pending |
| 3 | IDE-MCP `openDiff` (sole edit feed) — real handshake, render to Monaco | ⏳ Pending |
| 4 | Clickable `file:line` (OSC 8 + regex link provider → reveal in Monaco) | ⏳ Pending |
| 5 | Side-by-side two-pane Solid chrome | ⏳ Pending |

---

## Step 0 — Foundation ✅

**Done.** Whole solution builds with **0 warnings**; **25 T1 tests green**.

- **Branch:** `overnight/first-prototype`.
- **Build gates** (`Directory.Build.props`, applies to all projects): `Nullable=enable`,
  `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`,
  `EnforceCodeStyleInBuild=true`, `ImplicitUsings=enable`, `LangVersion=latest`.
  *Verified the gate bites:* it failed the build on a `CA1422` (obsolete `ActivateIgnoringOtherApps`)
  in the throwaway spike — exactly the behavior we want.
- **Central Package Management** (`Directory.Packages.props`): pinned xunit 2.9.3,
  xunit.runner.visualstudio 3.0.2, Microsoft.NET.Test.Sdk 17.12.0.
- **Interface seams** (the vault Build-Philosophy testability seams; real impl + in-memory fake, no fallbacks):
  - `IFileSystem` → `LocalFileSystem` (real) / `InMemoryFileSystem` (test fake).
  - `IDocumentModel` → `InMemoryDocumentModel` (T1 fake over a tiny `TextBuffer` with Monaco
    line/column semantics). Prod Monaco-proxy impl arrives in step 1/3.
  - `IDocumentModelFactory` → `InMemoryDocumentModelFactory`.
  - Edit feed modeled as `DiffProposal` (shaped exactly like MCP `openDiff`:
    old_file_path / new_file_path / new_file_contents / tab_name) resolved by a `DiffSession`
    (blocking Keep/Reject → `DiffOutcome` mapping to FILE_SAVED / DIFF_REJECTED).
- **Example-flow T1 test (the spine), green first:** openDiff-shaped edit → document model →
  user types into the proposed diff → Keep → save → assert in-memory FS has the saved content.
  Plus Reject-leaves-FS-untouched, new-file creation, double-resolve guard, and range-math unit tests.

**Decision (logged):** `AnalysisLevel=latest` with the *default* analysis mode (not `All`) — the
quality bar specifies `latest`; `All` would add high-friction style churn overnight for little gain.
Code-style enforcement (`EnforceCodeStyleInBuild`) is on globally and the generated AppKit bindings
pass it.

---

## Latency numbers

_(harness results land here in step 1)_

## Billing method + evidence

_(lands in step 2)_

## MCP handshake — what was reverse-engineered

_(lands in step 3)_

## Prioritized next steps

_(lands at the end)_
