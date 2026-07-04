# E2E flake analysis (Windows-dominated)

Status: living document — root causes confirmed where noted, open where noted
Last updated: 2026-07-04

A forensic catalog of the e2e suite's flakes, their confirmed/suspected root causes, and the
techniques that produced those findings. Retries are off by policy (a flake fails the run), so every
flake here is a red CI run someone has to eat. **Do not "fix" any of these with a retry, a skip, a
loosened assertion, or a wider timeout — those bury the defect (see CLAUDE.md).**

## The shape of the problem

Across the last ~120 CI runs, **~34 of 37 e2e-assertion flakes were Windows-only.** macOS and Linux
are nearly clean (a handful of 1-off flakes). The Windows flakes cluster on **PR/diff session
switching + editor rendering** — every frequent offender waits on editor/Monaco diff content or the
inline toolbar becoming visible after a session switch.

Ranked (occurrences in-window):

| # | test | file:line | OS | count | symptom |
|---|---|---|---|---|---|
| 1 | S2-race rapid PR→PR→PR switch | `pr-switch-race.spec.ts:27` | Windows | ~10 | changed-file set leaks a cross-PR file (`toEqual` mismatch) |
| 2 | S3 stale per-file diff on non-PR session | `pr-session-switch.spec.ts:77` | Windows | ~9 | `.weavie-inline-added` / view-lines never visible |
| 3 | Diff Against HEAD | `diff-against.spec.ts:32` | Windows | ~7 | `.weavie-inline-toolbar` never visible — **reddens main** |
| 4 | socket/buffer-exhaustion setup class | (varies) | Windows | ~5 | `net::ERR_NO_BUFFER_SPACE` / `#splash` setup timeout |
| 5 | session lifecycle @cross | `session.spec.ts:7` | Windows | ~4 | co-fails with the PR-switch storms |

macOS 1-offs: `diff.spec.ts:28` (DIFF_MARKER), `font-zoom.spec.ts:17` (font read `NaN` — already
guarded by the `expect.poll` on line 24). Linux 1-off: `terminal-reattach.spec.ts:32` (alt-screen
`null` — claude pane not yet registered in `__WEAVIE_TERMINALS__` within the poll budget).

## Confirmed root cause: #2 (S3) — the editor lays out at 5px and (on Windows) never recovers

**Symptom, from the trace DOM:** the `.monaco-editor` is `width:768px; height:5px`. The changed-line
band (`.weavie-inline-added`) exists but is `hidden` — the `.view-line` sits at `top:22px`, below the
5px viewport, clipped by `overflow:hidden`. The pane-slot rect is healthy (`60% × 100%`), so the
**layout tree is fine**; the editor container `.editor` itself was 0-height when Monaco measured it.

**Why exactly 5px:** Monaco's `ElementSizeObserver.measureReferenceDomElement` clamps with
`Math.max(5, clientHeight)` (`@codingame/monaco-vscode-api/.../config/elementSizeObserver.js`). A
0-height container ⇒ clamped to 5.

**Why it doesn't self-heal:** `automaticLayout: true` installs a `ResizeObserver` on the container
(`editorConfiguration.js:39`) whose callback re-measures + `layout()`s **synchronously** (the rAF in
`update()` only debounces a *second* same-frame observation). Reproduced locally: force `.editor` to
0-height, open the PR diff → Monaco goes to `742×5`; remove the clamp → it recovers to `742×709`
within one frame. **Recovery is reliable on an idle machine even from `display:none`→visible.**

**Therefore:** in the real Windows failure the container `.editor` **stayed 0-height for the full
15s** — a genuine layout stall, not a Monaco quirk. It correlates with a `net::ERR_NO_BUFFER_SPACE`
console error at page load (a failed resource load; see #4). Working hypothesis: under Windows
resource stress the boot/first-layout is disrupted enough that the editor container never settles to
its real height in the test's budget. **Not reproducible on Linux even oversubscribed** — it needs
the real Windows runner stress.

Next step to close it: the `viewport-layout.json` failure attachment (added in `fixtures.ts`, commit
`5376822`) now captures `editor` and `monaco` `getBoundingClientRect` on failure. The **next** Windows
S3/diff-against failure will show definitively whether `.editor` is `Wx0` (layout stall — chase the
flex/pane-slot chain or the boot ordering) or full-size with `monaco` at `Wx5` (a Monaco
non-recovery — force an explicit `layout()`). Until that datum exists, a "fix" is a guess.

## CONFIRMED + FIXED: #1 (S2-race) — a test walk-race, not a product bug

**Symptom:** after a PR→PR→PR switch storm settling on #101 (files `feature.ts`, `hello.ts`),
`collectChangedFiles` returned `[feature.ts, hello.ts, notes.txt]` — `notes.txt` is **#102's** file.

**Root cause (from the forensics, Windows run 28699904571):** at failure time the editor was healthy
(742×709), `console-errors.txt` was empty, and — decisively — the new `__WEAVIE_REVIEW__` attachment
showed the web's live `reviewFiles` was **already the correct `[feature.ts, hello.ts]` (label "PR #101")**.
So the host did **not** push a mixed set and the final state is correct. The defect was in the test:
`collectChangedFiles` walks the navigator over ~2 s (focus + `Ctrl+→` per file), and the storm's
per-switch pushes settle asynchronously — so the navigator *label* can already read a #101 file
(`awaitNavigatorOn` passes) while the *set* is still mid-swap, and the walk steps onto a transient #102
file before the set converges, recording it. A rapid 12-click storm legitimately flickers through
intermediate sets; only the settled state is the contract.

**Fix:** gate the walk on the actual set. `awaitReviewSet(page, [...])` polls `__WEAVIE_REVIEW__.files`
(by basename) until it equals the expected set, then `collectChangedFiles` runs against stable data.
Not masking: a *permanent* leak never settles ⇒ the poll times out ⇒ the test still fails. Applied to
`pr-switch-race.spec.ts` and `pr-two-sessions.spec.ts` (same pattern, same latent race); the too-early
`awaitNavigatorOn` (label-only settle) was removed. This is the payoff of the forensics — the missing
datum turned a suspected host bug into a confirmed one-line test-settle fix.

## #4 — `net::ERR_NO_BUFFER_SPACE` (Windows socket/buffer pressure)

Windows `WSAENOBUFS`: the OS couldn't allocate a socket buffer. Serialized runs (Windows is
`workers: 1`), so it's not parallel workers. Ruled out: the LSP reconnect is bounded and multiplexes
over the single bridge WS (no per-attempt socket); the harness's own HTTP polling was already removed
(the ready line is parsed from stdout). Most likely an environmental symptom of a resource-starved
hosted Windows runner, possibly compounding #2. `console-errors.txt` is now attached on failure so its
frequency/co-occurrence with #2/#3 is measurable.

## Reproduction & forensics techniques that worked

- **Parse the Playwright trace DOM directly.** `trace.zip` → `0-trace.trace` is JSONL; `frame-snapshot`
  events hold the serialized DOM (nested `[tag, attrs, ...children]`). Walking it for inline styles gave
  the exact `monaco-editor` `768×5`. Console/`ERR_` lines are in the `log`/`console` events.
- **`viewport-layout.json`** (failure attachment) is the fastest ground truth for a layout collapse —
  it records `app`/`layoutRoot`/`editor`/`monaco` rects + `visualViewport.scale`.
- **Local Monaco layout probe**: launch the built headless host, drive the PR flow, and toggle a CSS
  clamp on `.editor` to force/heal the 0-height — proves Monaco's recovery behaviour without CI.
- **CPU oversubscription** (`--repeat-each=N --workers>cores`) did **not** reproduce the Windows races
  on a fast Linux box — these need the genuinely slow, resource-limited hosted Windows runner.

## Guidance for the next agent

- Windows flakes are **not** locally reproducible or verifiable here. Land a reasoned fix, then let
  CI validate it across several runs — don't claim it fixed without the runs.
- Get the failure datum first (`viewport-layout.json` / `console-errors.txt` / `__WEAVIE_REVIEW__`),
  then fix. Two of the top three flakes are one attachment away from a decisive root cause.
- Never mask: no retries, no `test.skip`, no widened ceiling, no "re-ran and it passed."
