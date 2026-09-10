# E2E coverage consolidation

The first consolidation pass retains the behavior assertions from 19 overlapping test cases in
shorter sets of independent journeys. Each journey still owns its isolated fixture. Named steps
identify the individual behavior when a combined test fails.

## Coverage map

Counts include configured project expansion, measured with `playwright test --list --reporter=json`.
Paths below are relative to `src/web/e2e/`.

| Spec | Before → after | Retained coverage |
| --- | --- | --- |
| `agent-composer.spec.ts` | 43 → 38 | Provider control labels and model/reasoning/boolean selection; both picker highlights across host updates; approval shortcut, response, and live resolution. Each selection waits for a message after its own checkpoint. |
| `functional/diff-review.spec.ts` | 25 → 23 | Keyboard keep/undo counts and caret reveal, faded accepted bands, inline keep/undo buttons. The first-hunk reveal test subsumes the duplicate keep/undo test. |
| `functional/editor-cursor.spec.ts` | 3 → 1 | Glyph alignment across columns, insertion at the caret, and alignment with completion visible, with the same pixel tolerances. |
| `functional/editor.spec.ts` | 7 → 6 | Omnibar open and initial syntax tokens precede incremental tokenization after typing. |
| `functional/focus.spec.ts` | 5 → 3 | Editor-to-shell focus, unchanged editor text after terminal typing, and shell-to-agent focus. |
| `functional/font-zoom.spec.ts` | 3 → 2 | Palette category/discoverability and live increase/decrease/reset. The keyboard empty-toast regression stays separate. |
| `functional/git-blame.spec.ts` | 11 → 9 | Default single-line annotation, click-to-open and dismissal; toggling annotations and invoking Show Blame while they are off. |
| `functional/media.spec.ts` | 8 → 7 | Cold image decoding, existing text working copy surviving media activation, stable media source, and reload of both tabs with the image active. |
| `functional/menu.spec.ts` | 2 → 1 | Keyboard traversal/dismissal followed by mouse toggling, submenus, shortcut labels, and command dispatch. |
| `functional/palette-focus-gated.spec.ts` | 3 → 2 | Terminal Copy visibility, browser Paste exclusion, and Copy exclusion after editor focus. The held-frame focus race stays separate. |
| `functional/session.spec.ts` | 14 → 13 | Clean session deletion, completion feedback, protected workspace menu, and survival of workspace identity and contents. |

## Suite size and limits

| Project | Before | After |
| --- | ---: | ---: |
| chromium | 89 | 84 |
| headless | 292 | 278 |
| remote | 30 | 30 |
| mobile | 14 | 14 |
| **Total per platform** | **425** | **406** |

That removes 19 test executions in normal Linux PR validation, including 14 full-stack host startups;
full three-platform validation saves 57 executions and 42 host startups. Remote transport and mobile
coverage stay intact. Consolidated tests use the existing
timeouts, retry settings, and assertions; no failing platform or scenario is excluded.

This is a 4.5% reduction in test executions, not a measured reduction in flake rate. Remaining actions
can still race, and a failure early in a journey prevents its later steps from running. Keep journeys
short and related; use the [test-boundary criteria](integration-testing-strategy.md#choosing-test-boundaries)
to evaluate further reductions rather than targeting an arbitrary test count.
