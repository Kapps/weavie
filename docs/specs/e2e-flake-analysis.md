# E2E flake analysis (Windows-dominated)

Status: living document — root causes confirmed where noted, open where noted
Last updated: 2026-08-20

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

## CONFIRMED + FIXED: the 5px clamp renders ONE line, so a locator for any other line matches nothing

**Symptom (issue #625, Windows run 31993224310):** `editor-peek-definition.spec.ts:84` burned its full
60s on `wordToken(...).click()`. The call log stops at `waiting for locator(...)` with no
`locator resolved to` line — the locator matched **nothing** for the whole budget, so this is not an
actionability failure. `viewport-layout.json` read a healthy `742×709` for both `.editor` and
`.monaco-editor` and `console-errors.txt` was `(none)`, so neither reading #2 predicts (`Wx0` stall,
`Wx5` non-recovery) was present.

**What those rects cannot tell you:** they are captured at *teardown*. Reproduced locally — force
`.editor` to 0-height and Monaco clamps its viewport to `max(5, 0) = 5` and renders **only line 1**;
every later line is absent from the DOM entirely. Restore the height and it is back to all 7 lines
within a frame. A *transient* collapse therefore leaves healthy rects at teardown and an unfindable
line during the test — exactly the datum shape observed. The lesson generalizes: `data-active-file`
says which file the editor holds, never that it has room to draw it.

**Fix:** `openFile` now waits for Monaco's viewport to agree with its container (`awaitEditorLaidOut`,
`e2e/harness/actions.ts`) before any spec addresses rendered text. A transient collapse is waited out;
a permanent one fails at once naming the clamp, instead of spending the test timeout on a locator that
could never match. The healthy teardown rects are evidence this occurrence *did* recover, so waiting is
a real fix here and not just a better error message.

**Datum added:** `viewport-layout.json` now also carries `monacoViewportHeight`, `modelLineCount`, and
`renderedLines` — which separates "collapsed while the test ran" from a genuinely healthy editor at a
glance, the distinction the rects alone could not make.

**2026-08-18 recurrence, run 32096266021:** the same symptom hit a *different* line in the same file —
`editor-peek-definition.spec.ts:133` ("alt+click during a multicursor session adds a cursor instead of
peeking"). Identical fingerprint: call log stuck on `waiting for locator(...)` for the full 60s,
`renderedLines` at teardown was `[""]` (exactly one, empty line — the 5px-clamp signature) while
`.editor`/`.monaco` read a healthy `742×709`. `awaitEditorLaidOut` didn't cover this one because the test
calls `editor.setSelections(...)` through `page.evaluate` *after* `openFile` and then addresses rendered
text again — a second window for the same transient collapse that `openFile`'s guard doesn't span. Fixed
by calling `awaitEditorLaidOut` again right before that click, rather than widening `openFile`'s guard to
every possible later mutation. If a third call site turns up the same way, that's the signal to stop
patching individual sites and gate `wordToken`/`altClick` themselves on layout instead.

**2026-08-18 third occurrence, run 32104522458:** hit *again*, same file, same original line —
`editor-peek-definition.spec.ts:84` ("Alt+F12 peeks the definition of the symbol at the cursor",
[job 95611908477](https://github.com/Kapps/weavie/actions/runs/32104522458/job/95611908477)) — despite
that test going through `openFile`'s guard via `focusEditor` just a few lines earlier, with only a plain
click and a `page.evaluate` (no `setSelections`) in between. Same fingerprint again: 60s stuck on
`wordToken(...).click()`, healthy rects at teardown. This is exactly the third call site the prior
occurrence predicted, so per that note the per-site patching stopped: `wordToken` (the shared helper both
flaked tests route through) now calls `awaitEditorLaidOut` itself before building its locator, so every
caller re-waits for layout for free instead of each test needing its own reasoning about what might have
relaid-out since `openFile`. The multicursor test's standalone `awaitEditorLaidOut` call (added for the
previous occurrence) was removed as redundant.

**2026-08-20 fourth occurrence, run 32333399943:** hit a fourth time on the multicursor test
(`editor-peek-definition.spec.ts:117`, ["alt+click during a multicursor session adds a cursor instead of
peeking"](https://github.com/Kapps/weavie/actions/runs/32333399943/job/96318875803)) — main's post-merge CI
for PR #639 — **despite** the shared-helper gate from the third occurrence already covering this call site.
Identical fingerprint once more: `renderedLines: [""]`, `.editor`/`.monaco` healthy (742×709) at teardown,
`console-errors.txt` empty, `Error: locator.click: Target page, context or browser has been closed` (the
final rejection Playwright surfaces on a `word.click()` still pending when the 60s test timeout tears the
page down — not a new symptom, the same never-resolved locator as the prior three).

**Root cause of the gate's miss:** `awaitEditorLaidOut` only compared `editor.getLayoutInfo().height` to
the container's `clientHeight`. Monaco's `ResizeObserver` callback re-measures and calls `layout()`
synchronously on a resize, but "the numbers agree" is a read taken at one instant over a CDP round-trip —
it proves the clamp isn't active *at that instant*, not that the DOM has actually caught up rendering every
line back in (or that a fresh collapse hasn't started by the time the click's own separate round-trip
resolves the locator). The height diff is a proxy for the actual defect; **the real defect, per every one of
these four occurrences' forensics, is the DOM only holding the clamp's one-line placeholder.**

**Fix:** `awaitEditorLaidOut` now also polls the actual DOM: it requires more than one `.view-line` element
present whenever the model has more than one line, alongside the existing height check. This checks the
documented failure signature directly instead of a derived number that can agree while the render lags. Not
a retry, skip, or widened timeout — `expect.poll`'s existing polling now waits on a truer condition. Land
and soak per the guidance below; this is not reproducible locally so it can only be validated by watching
Windows CI stay clean on this test across subsequent runs.

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

**Fix:** gate the walk on the actual set having **quiesced**. `awaitReviewSet(page, [...])` reads
`__WEAVIE_REVIEW__.files` (by basename) and requires the target set to hold across a sustained run of
reads — the push train has drained and nothing more bounces — then `collectChangedFiles` runs against
stable data. Applied to `pr-switch-race.spec.ts` and `pr-two-sessions.spec.ts`; the too-early
`awaitNavigatorOn` (label-only settle) was removed. Not masking: a *permanent* cross-PR leak quiesces to
the WRONG set, which never equals `want`, so the poll times out and the test still fails.

> **Correction (second CI cycle):** the first attempt gated on a *momentary* match (`.toEqual(want)`
> once). It still flaked — the forensics on the re-fail again showed the editor healthy and the settled
> set correct, so the walk had again caught a transient. The storm's 12 rapid clicks queue faster than
> the host drains them, so the set bounces through intermediate #101 states; a momentary match landed on
> one *before* the last switch's push, and the ~2s walk overlapped the remaining drain. The settle signal
> after a *burst* must be steady-state, not first-match.
>
> **Hardening (third cycle):** list-comparison quiescence still poll-*samples* — a fast bounce can
> round-trip between two ~100ms reads and be missed. So `__WEAVIE_REVIEW__` now carries a monotonic `rev`
> that bumps on every change; `awaitReviewSet` waits for `rev` to stop advancing (and `files` to match),
> which cannot miss a bounce. Note this flake was **not reproducible locally even at 10× CPU throttle +
> oversubscription** — it is purely a slow-hosted-Windows-runner timing artifact, so the fix is validated
> by soaking across Windows CI re-runs, not locally.

## 2026-07-23 occurrence: #5 (session lifecycle @cross), run 29973556071

Windows/`remote`, `session.spec.ts:10` (`create, switch, unload, and reopen sessions @cross`) — the
`#splash` wait timed out at 40s during the auto `weavie` fixture's boot, before the test body ran at all.
Landed on the merge of PR #440 ("Fail loudly when switching to a session on an unreachable backend");
confirmed **not** a regression from that PR — its diff touches only `App.tsx`/`bridge.ts`/`SessionRail.tsx`/
`session-store.ts` and a Codex test-helper rename, nothing on the boot path this fixture exercises before
`use(host)`.

No new datum: this run carries the same gap the "Guidance for the next agent" section below complains
about — `console-errors.txt`/`weavie-host.log`/`viewport-layout.json` were never attached. The first
attempt to fix that still gated its `finally` on `testInfo.status`, but Playwright does not mark a setup
failure until after the fixture unwinds. The fixture now catches setup failures directly and attaches
diagnostics unconditionally before teardown.

## CONFIRMED + FIXED: #4 — `net::ERR_NO_BUFFER_SPACE` (Windows socket/buffer pressure)

Windows `WSAENOBUFS`: the OS couldn't allocate a socket buffer. Serialized runs (Windows is
`workers: 1`), so it's not parallel workers. Ruled out: the LSP reconnect is bounded and multiplexes
over the single bridge WS (no per-attempt socket); the harness's own HTTP polling was already removed
(the ready line is parsed from stdout). Most likely an environmental symptom of a resource-starved
hosted Windows runner, possibly compounding #2.

**2026-07-31 occurrence, run 30612700118:** the remote media fixture never left its splash because
Chromium failed to fetch `rolldown-runtime-*.js` with `net::ERR_NO_BUFFER_SPACE`. The trace showed the
entry page opening nine resources at once, including an eager 7.9 MB Monaco chunk. `App.tsx` statically
imported the test-lens command, pulling Monaco into the entry graph despite the editor's lazy boundary;
Vite then emitted module-preload links for that graph, creating the startup socket burst.

**Fix:** the test-lens command now loads behind its existing editor boundary, and the production build
disables eager module preloads. The generated entry page no longer references Monaco JavaScript or any
preload: it starts with the entry module, three stylesheets, and the logo, while module dependencies use
the established HTTP connections. Session-URI ownership parsing is separate from Monaco URI construction,
so shell code can route an editor model without importing the editor runtime. The build traverses each
entry's static chunk graph and fails if that boundary regresses. This repairs both the accidental eager
7.9 MB download and the Windows socket-allocation failure without a retry, skip, or wider timeout.

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
