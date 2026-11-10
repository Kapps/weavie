# Design system — brief

## Goal

Weavie's chrome (menus, dialogs, panels, tabs, cards) looks like one product: a few text sizes, one bold,
one section-label style, rows and controls that line up — and new UI gets that from shared roles and
primitives instead of per-file discipline. The look stays today's technical/monospace character with the
mismatches removed; this is not a restyle. Existing inconsistencies may change wherever the result is better
for the user; that judgement is delegated to the implementer.

## Evidence

A census of 40 surfaces of the real app at default settings (`temp/design-census/REPORT.md`, regenerable from
its harness) measured:

- 32 distinct rendered font sizes; 88% of text sits on 11/12/13/16px, the rest come from ad-hoc `em` multipliers.
- `--font-metadata-size` derives from the editor size (default 16px), so "secondary" chrome text renders at
  13px, larger than 12px chrome (picker headings and theme-picker metadata outsize the items they describe).
- 5 dialog-title styles. `.confirm-title` sets no size of its own, so it inherits 13px or 19.2px depending on
  the dialog; 13 controls render at the browser's 13.33px because controls inherit only the font family.
- 9 uppercase label styles, 6 shortcut-hint sizes, 3 "bold" weights (600/650/700), 15 line-height ratios.
- 10 single-line row heights (24–33.7px) and 14 text-button heights.
- 20 distinct rows that centre mixed-size text are off-baseline by 1–1.5px; every row using baseline alignment
  is exact.

## Scope and decisions

1. **Chrome type roles, fixed px, independent of the editor font.** Tokens are the source of truth (they must
   reach shadow roots and CSS written in TypeScript); role classes consume them.

   | Role | Size | Weight | Use |
   |---|---|---|---|
   | title | 13 | 600 | dialog and pane titles |
   | body | 12 | 400 | default chrome text |
   | secondary | 11 | 400 | descriptions, metadata |
   | section label | 11 | 600, uppercase, one tracking value | the only chrome section-heading style |
   | shortcut / badge | 10 | 600 | key hints, counts |

   Chrome roles share one line height so mixed sizes occupy the same line box (a taller one for wrapping
   body text). Emphasis is the same role at 600; 650 and 700 leave chrome.
2. **Two secondary roles replace `--font-metadata-size` and `--font-hint-size`**: chrome-secondary (fixed
   11px) and content-secondary (derived from the editor font, as today). Small text inside content scopes —
   agent-pane entry labels, status and working lines, search line numbers — stays content-relative and keeps
   following font zoom; only chrome consumers move to the fixed role.
3. **Content keeps following the editor font**: code, the agent transcript, rendered markdown, fields where
   the user types a prompt, and popup rows that show content-sized text (slash menu, search previews, prompt
   suggestions). `em` survives only inside prose (headings, inline code).
4. **Inherited sizes are fixed at the source.** Controls inherit the full font, not just the family. The
   existing `ModalShell` gains title, body and actions slots so every dialog title is the title role; the
   13px modal default and the `.session-prompt` 16px scale go away (Diff Against, Open PR, Register Agent,
   Source Token become ordinary compact dialogs, with their font-relative padding and widths set explicitly).
5. **Alignment by construction.** Text beside text aligns on the baseline; icons and controls sit in a box
   one line tall. A row's height is its text's line box plus fixed padding, so chrome rows land on one height
   and content-text rows grow with the editor font instead of clipping. Buttons take a small set of heights.
6. **Extend the existing primitives, do not replace them**: `ModalShell`, `PromptButton`, `ContextMenu`,
   `ControlMenu`, plus shared CSS classes for the flat list/menu row (optional description line, shortcut
   slot), button, section label, shortcut hint and text field. Existing class names stay where the e2e suite
   selects on them (e2e is not type-checked).
7. **All raw font sizes, weights and line heights in feature CSS migrate to the roles**, on every surface
   including mobile, onboarding, PR comments and the inline review tags (which take the chrome font).
8. **Enforcement stays cheap.** A Biome plugin in the existing `biome check` gate rejects raw `font-size`,
   `font-weight`, `line-height` and `font` outside the token file, primitives and prose rules. What a source
   lint cannot see (alignment, heights, UI text) goes into AGENTS.md and the `weavie-reviewer` instructions.
   No rendered-output test is committed.
9. **UI-text rules in AGENTS.md**, short and checkable: a surface gets a title and control labels; any other
   line needs a reason (warning, input format, limitation, keyboard hint); no description by default — rename
   the options instead (data-supplied descriptions are exempt); no heading restating its container; tooltip =
   command name + shortcut; no "you can"/"please"/trailing periods. Surfaces breaking these are trimmed as
   they migrate.
10. **One branch, one PR.** AGENTS.md's typography section and a new concept doc describe the roles and
    primitives.

## Edge cases

- **Visible shrink, accepted by the user**: context menus 13→12px with shorter rows, chrome metadata 13→11px,
  picker headings 13→11px, the four `.session-prompt` dialogs lose their 16px scale.
- **Chrome small text stops following the editor font.** Body chrome was never resizable; this is accepted.
- **Monospace in chrome** (branch names, paths, key chips): mono emphasis uses the mono bold weight (Go Mono
  ships only 400/700); mono beside proportional text aligns on the baseline.
- **Mobile**: touch targets keep their larger heights as variants of the shared rows and controls; the
  `.app.compact` overrides keyed on desktop class names keep working.
- **Display type**: one display role for getting-started, empty-state and page headings; the welcome window
  keeps a larger landing scale (a hero and a lead role) rather than free sizes.
- **Heights baked into code** (review section header, diff badge zone, review gap) must still match their CSS
  after migration.
- **WebKit minimum** (macOS 14 / WebKitGTK 2.42 ≈ Safari 17): no `text-box-trim`, `cap` units or relative
  colour syntax; `lh`, `color-mix` and `@layer` are available.
- **Two-line and wrapping rows**: the icon/shortcut stays on the first line.
- **Monaco and xterm internals** are untouched.

## Not doing

- **`interface.font.size` setting**: chrome has ~72 px heights, 47 fixed icon sizes and heights baked into
  code that would all have to scale; too much work for a knob nobody has asked for.
- **Spacing scale, corner radii, shadows, derived colour tokens**: large churn for little visible gain.
- **A committed rendered-output invariant test.**
- Changing fonts, theme colours, layout, surface structure, or the content scales (transcript and
  markdown-preview heading sizes stay as they are).
- The suggestion card not appearing for a page that connects after the probe (observed during the census;
  separate bug).

## Approach

- **Repair the broken sources of truth, then migrate.** Most measured defects trace to a few causes: one
  metadata token serving chrome and content, controls and dialog titles inheriting whatever size surrounds
  them, and per-file `em` multipliers. Fixing those at the source removes whole classes of mismatch that a
  lint on written values could never catch.
- **Roles and sizes follow established dense-UI systems** (VS Code's token ramp, Apple's macOS text styles,
  Primer, Radix): 3–4 chrome sizes, two weights, a fixed line height per size.
- **Verify by measurement during the build, not by eye.** The user will not review visuals. Acceptance is the
  census harness re-run — extended to the surfaces it did not reach (mobile, PR comments, finish-review,
  workflow cards, remote panel, remaining dialogs) — showing only role sizes/weights in chrome, no baseline
  drift in rows, and the reduced height set; before/after screenshots of every surface inspected by the
  implementer; `weavie-reviewer` and `weavie-tester` on the result; the full e2e suite; `pnpm e2e:perf` for
  editor-adjacent chrome.
- **Implementation runs on Opus subagents** (user instruction); planning stays with the orchestrating model.
