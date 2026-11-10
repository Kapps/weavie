# Design system

Weavie's chrome (menus, dialogs, panels, tabs, cards) gets its type, alignment and heights from a small set
of **role tokens** in `src/web/src/styles.css` and **primitive classes** in `src/web/src/primitives.css`,
so a new surface looks like the rest of the product by construction rather than by per-file discipline.
A Biome plugin rejects raw typography; the `weavie-reviewer` agent covers what a source lint cannot see.

## Why

A census of 40 rendered surfaces found 32 font sizes, 5 dialog-title styles, 9 uppercase label styles,
6 shortcut-hint sizes, 3 bold weights, 15 line-height ratios, 10 single-line row heights and 14 text-button
heights, plus 20 rows whose centred mixed-size text sat 1–1.5px off the baseline. The causes were few: one
metadata token shared by chrome and content, controls and dialog titles inheriting whatever surrounded
them, and per-file `em` multipliers. The roles and primitives remove those sources.

## Chrome roles

Chrome is fixed px and does not follow the editor font. Each role is one `font` shorthand built from atomic
size, line and weight tokens; consumers write the shorthand.

| Role | Size / line / weight | Token | Use |
|---|---|---|---|
| hero | 36 / 40 / 600 | `--font-chrome-hero` | welcome window wordmark |
| display | 20 / 24 / 600 | `--font-chrome-display` | page headings: Getting Started, editor empty state, session inbox |
| lead | 16 / 24 / 400 | `--font-chrome-lead` | welcome tagline, recent rows, `.ui-button-xl` |
| title | 13 / 16 / 600 | `--font-chrome-title` | dialog, popover and card titles |
| body | 12 / 16 / 400 | `--font-chrome-body` | single-line chrome: rows, buttons, fields, pane headers (+ emphasis) |
| body-wrap | 12 / 18 / 400 | `--font-chrome-body-wrap` | wrapping text; `body`'s inherited default |
| secondary | 11 / 16 / 400 | `--font-chrome-secondary` | descriptions, metadata; section labels at 600 |
| badge | 10 / 16 / 600 | `--font-chrome-badge` | key hints, counts |
| mono | 12 / 16 / editor weight | `--font-chrome-mono` | branch names, paths and other mono text in chrome |

Hero and lead exist only for landing surfaces. The family is `--font-chrome-family` (the
`interface.font.family` setting); mono uses `--font-content-family`.

- **Line heights.** Every single-line role uses 16px, so 10–13px text shares one line box. Wrapping body
  text uses 18px, which is also what everything inherits.
- **Emphasis** is the same role followed by `font-weight: var(--font-weight-emphasis)` (600), or
  `--font-weight-mono-bold` (700) in the mono face, which ships only 400/700. There is no other bold.
- **`font:` goes first in a rule.** The shorthand resets `font-style` and `font-variant-numeric`; restate
  them after it.
- **Section labels** (the only uppercase style) are `.ui-section-label`: secondary at 600 with
  `--font-label-tracking`.

## Content and prose roles

Content roles follow the editor font — `font.size` and `editor.font.size`, including the font-zoom commands
— through the `--editor-font-*` variables `fonts.ts` publishes.

| Token | Value | Use |
|---|---|---|
| `--font-content` | editor family, size and weight; line 1.24 | code-adjacent text: agent transcript and composer, search results, slash menu, prompt suggestions, fields where the user types a prompt |
| `--font-content-secondary-size` | editor size × 0.8125 | small text inside a content scope (entry labels, status lines, line numbers) |
| `--font-content-title-size` | editor size × 1.3 | headings inside a content scope |
| `--font-content-row-line` | editor size × 1.5 | line box of content rows, buttons and fields |
| `--font-prose` | prose family, editor size; line 1.5 | rendered documents: agent markdown, preview, plans, PR comments |

`em` font sizes are allowed only in prose: the four `*.prose.css` files (`agent/agent-markdown.prose.css`,
`editor/preview/preview.prose.css`, `editor/plan.prose.css`, `editor/comment.prose.css`) and `SOURCE_STYLES`
(`editor/source/source-styles.ts`), prose CSS held in TypeScript. New chrome geometry is px; `em` geometry belongs
where it should scale with the content font, as in the agent transcript and the preview, though some older chrome
rules still use it (connection banner, update card, shell tabs, parts of the review view).

## Primitives

All live in `primitives.css` inside `@layer ui` and own geometry and type plus button and field states;
row hover and selection colours stay in feature CSS. Add the primitive class beside the feature class.

- **`.ui-row`** — 26px list/menu row, baseline-aligned, body role. Variants: `-compact` (24px), `-content`
  (editor-font line box), `-stack` (multi-line; `min-height` instead of a fixed height). Slots:
  `.ui-row-text` (label column), `.ui-row-desc` (secondary, dim), `.ui-row-end` (trailing shortcut or meta).
- **`.ui-icon`** — inline box one line tall (`--row-line`, which a `.ui-button` sets to its own line),
  top-aligned, its `svg` at `--icon-size`.
- **`.ui-button`** — 24px text button. Variants: `-lg` (28px), `-xl` (40px, lead role; landing surfaces),
  `-content` (editor-font sized), `-primary`, `-danger`. **`.ui-icon-button`** — 20px square, no border.
- **`.ui-field`** — 28px text input. Variants: `-mono`, `-content` (editor-font sized).
- **`.ui-section-label`** — the section heading style.
- **`.ui-kbd`** — key hint chip, badge role, on whatever element holds the hint (`<span>`, `<kbd>`,
  `<small>`); `-plain` drops the chip border. Inside a button it is `aria-hidden` so the accessible name
  stays the label.
- **`ModalShell`** (`chrome/ModalShell.tsx`, styled by `editor/confirm-dialog.css`) — every dialog. It
  renders the title role in `header.confirm-head > h2.confirm-title` (with an optional `titleEnd`), the body,
  and `footer.confirm-actions`; a surface that draws its own display heading passes `labelledBy` instead of
  `title`. **`PromptButton`** is its action: `.ui-button.ui-button-lg` plus an optional `.ui-kbd` hint.

## Alignment and height

- **Text beside text aligns on the baseline**, never `align-items: center`: centring mixed sizes leaves the
  smaller text off-baseline.
- **Rows and controls are fixed-height.** Baseline alignment of mixed sizes makes an auto-height row grow by
  a sub-pixel (26 vs 26.5px), so `.ui-row` sets `height: --row-line + 2 × --row-pad` and absorbs it. The
  same construction gives `-content` rows a height that grows with the editor font instead of clipping.
- **Icons and controls beside text** sit in `.ui-icon`, a box one line tall, top-aligned so they stay on the
  first line of a wrapping row.
- **Touch sizes** set the primitives' variables (`--row-pad`, `--control-h`, `--icon-button-size`,
  `--pane-head-h`) on the compact-mode selectors; a `min-height` on a fixed-height row would leave the text
  top-aligned.

```mermaid
flowchart TB
  subgraph row[".ui-row: height = --row-line + 2 × --row-pad = 16 + 2 × 5 = 26px"]
    direction TB
    padTop["--row-pad (5px)"]
    subgraph line["line box: --row-line (16px), children on one baseline"]
      direction LR
      icon[".ui-icon: one line tall, svg centred"] --- text[".ui-row-text: label (body 12px)"]
      text --- trail[".ui-row-end: .ui-kbd (badge 10px)"]
    end
    padBottom["--row-pad (5px)"]
    padTop --- line --- padBottom
  end
```

| Element | Heights |
|---|---|
| Rows | 26, compact 24, two-line stack 42, touch 44 |
| Text buttons | 24, `-lg` 28, `-xl` 40, touch 44 |
| Icon buttons / fields | 20 / 28 |

## Cascade layers

`styles.css` opens with `@layer base, ui;`.

- **`base`** — `body { font: var(--font-chrome-body-wrap) }`, `button, input, select, textarea { font:
  inherit }` (controls take the full font, not the browser's 13.33px) and `strong, b` at emphasis weight.
- **`ui`** — the primitives.
- **Unlayered** — feature CSS and the prose files, which beat both layers regardless of specificity.

Layers are required because the build emits component-imported CSS before `styles.css`, so source order
cannot decide whether a feature rule overrides a primitive. The control rule sits in `base` because an
unlayered `button { font: inherit }` would beat every `.ui-button`.

## Enforcement

- **Lint.** Two GritQL plugins, registered under `plugins` in `src/web/biome.json`, run in `biome check` over
  `src/**/*.css`. Property names match in any case.
  - `lint/typography.grit`: a `font`, `font-size`, `font-weight` or `line-height` must be `inherit` or
    exactly `var(--font-…)` of a listed token. The list is closed, so the plugin is the token registry: an
    invented `--font-my-thing` fails.
  - `lint/font-tokens.grit`: a `--font-*` custom property may be declared only in `src/styles.css`, so no
    feature rule can redefine a role token.
- **Exemptions.** `typography.grit` skips `fonts.css` (`@font-face` descriptors) and `*.prose.css`;
  `font-tokens.grit` skips only `styles.css`.
- **Reviewer.** The lint does not see `font-family`, CSS held in TypeScript (`SOURCE_STYLES`), inline
  `style`, alignment, heights or UI text. `AGENTS.md` states those rules and `weavie-reviewer` checks them:
  hand-styled controls, dialog titles outside `ModalShell`, centred mixed-size rows, `em` or literal
  heights, icons sized with `font-size`, non-prose rules in a prose file, and unexplained UI text.

## Adding a role or primitive

Prefer an existing role; add one only for a surface none of them serves.

- **Role** — define its atoms and shorthand in `styles.css` `:root` and add the name to the matching
  alternative in `typography.grit` in the same change, then add its row to the table above.
- **Primitive** — add it to `primitives.css` inside `@layer ui`, built only from tokens, and list it under
  [Primitives](#primitives) (and in `AGENTS.md` if it is a new kind of element).
