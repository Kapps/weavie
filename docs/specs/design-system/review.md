# Design system — review

Review of PR #1171 (merged as `05423ccb`) by a fresh-context `weavie-reviewer`, plus a WebKit A/B benchmark
of the unified review view. Claims marked "confirmed" were re-checked against the code.

## Verdict

No blocking defects. Four should-fix items and a set of nits, fixed in the follow-up PR on
`fix/design-system-review`.

## Should fix

| # | Finding | Where | Status |
|---|---|---|---|
| 1 | `.ui-icon` sits 4px high inside `.ui-button-xl` and `.ui-button-content`: the icon box takes the 16px chrome line while those variants use a 24px / content line. Nothing live hits it; the Welcome buttons dodge the primitive with `em`-sized svgs and a centre override. | `src/web/src/primitives.css` (`.ui-icon`), `src/web/src/welcome/welcome.css`, `Welcome.tsx` | Fixed |
| 2 | The typography lint can be bypassed: a feature rule may redefine a role token (`--font-chrome-size: 20px`), and upper-case property names (`FONT-SIZE: 13px`) are not matched. | `src/web/lint/typography.grit` | Fixed |
| 3 | The Open PR "no results" hint is absolutely positioned below its field and now overlaps the action row. | `src/web/src/chrome/session-prompt.css` (`.session-prompt-hint`) | Fixed |
| 4 | Docs overstate the rule: `em` geometry remains in several chrome stylesheets and `SOURCE_STYLES`; `.ui-kbd` is also used on `<kbd>` and `<small>`; touch overrides also use `--icon-button-size` and `--pane-head-h`. | `docs/concepts/design-system.md`, `AGENTS.md` | Fixed |

## Nits

- Duplicate selector block for `.app.compact .agent-compose-actions > button` in `src/web/src/mobile/mobile.css`.
- `DismissButtons` takes a `buttonClass` prop that one caller passes as `""` (`src/web/src/agent/input-request.tsx`).
- `size` props on icons inside `.ui-icon-button` are dead (`PrThreadCard.tsx`, `PrCommentItem.tsx`).
- Class names with no CSS and no e2e reference: `browser-filter-path`, `browser-icon`,
  `control-menu-option-desc`, `editor-empty-action-text`, `editor-empty-action-hint`, `gs-primary`,
  `session-overview-title`, `session-panel-key`, `welcome-mark`, `welcome-row-text`.
- `.weavie-pr-quote` is content-sized on a fixed 16px line with `overflow: hidden`; it clips once the editor
  font passes about 20px.
- The PR overview title scale moved from 1.45× to 1.3× of the prose size, an unlisted content-scale change.
- `em`-sized icons beside text without `.ui-icon` in Getting Started and Welcome.
- `e2e/capture-remote.mjs` selects the page-global `.ui-button-primary`.

The nits were deliberately left out of the follow-up to keep it small.

## Found sound

- Static gates: `tsc`, Biome with the plugin, vitest (1,185 tests).
- Cascade layers: the built bundle emits `@layer base` before `@layer ui` in the first-loaded chunk.
- Measured heights in Chromium and WebKit: rows 26 / 24 / 42, buttons 24 / 28 / 40 / 30, fields 28 / 34, pane
  head 24, review file header 32 (+2px border = `SECTION_HEADER_HEIGHT` 34).
- `ModalShell` slot checks, focus refs and accessible names; `RecreateSessionDialog` without its form;
  `ControlMenu` and `ContextMenu` navigation and offsets.
- No removed class or id is referenced in `src`, `e2e` or docs.
- Prose moves are verbatim; `tabular-nums` and italics are restated after every `font:` shorthand.
- Perf-sensitive editor code: class strings and one constant; no new layout reads, observers or DOM changes.
- e2e edits weaken nothing.

## Review-view performance (WebKit A/B, `origin/main` 575b0610 vs `aba6aa08`)

Alternating runs, medians. User-facing metrics are within run-to-run noise.

| Metric | 12 files: main | 12 files: change | 100 files: main | 100 files: change |
|---|---|---|---|---|
| Review open (ms) | 173 | 168 | 208 | 198 |
| File switch p50 (ms) | 103 | 105 | 119 | 120 |
| File switch p95 (ms) | 134 | 140 | 155 | 154 |
| Wheel frame-gap p95 (ms) | 28.5 | 29 | 39 | 41 |
| Visual step-backs while scrolling | 0 | 0 | 0 | 0 |
| Whole-page restyle after a root property change (ms) | 159 | 171 | 284 | 333 |

- The one consistent cost is the whole-page restyle after a theme or font change (+8–17%), attributed by
  ablation to the ~38 extra root custom properties, not to the `font` shorthands or cascade layers. Scrolling
  never triggers it.
- The rendered section header is exactly 34px, matching the estimate; on `main` it rendered 37.8px against an
  estimate of 42.
- Weakest result: dropped frames on the 100-file wheel run were 24% higher with overlapping ranges, taken
  under higher machine load; read as noise, not proven.

## Not covered

- The full e2e suite and `pnpm e2e:perf` were run by CI and the build stage, not by the reviewer.
- macOS WKWebView and Windows hosts were not rendered.
- Census-unreached surfaces: register-agent dialog, remote-agents panel, update card, connection banner.
