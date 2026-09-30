# Unified review scroll cost: where the frame time actually goes

Measured on the real Linux GTK4/WebKitGTK desktop build (RTX 4090, 3840x2160 @ 240 Hz, logical 2560x1440,
content viewport 1280x840 at DPR 2), in one isolated benchmark instance, five agent-applied 5,000-line files,
page-dispatched wheel at one notch per animation frame.

## Correction: the forced-layout framing below is wrong

Later measurement on the same native build retracts the central claim of
[The boundary](#the-boundary-and-why-only-half-of-it-is-fixable-cheaply). Forced Monaco layouts are **not** what
makes a file boundary expensive, so the band quantisation that shipped for them is not justified by this cost.

Timing every `editor.layout()` call rather than counting them: **253 calls, 137 ms total, mean 0.5 ms, max 2 ms,
none over 20 ms** (ghost-heavy fixture, 24 bursts). The 48-83 ms boundary frames each contain exactly one such
call, costing 0-1 ms. Counting layouts measured a correlate of the crossing, not its cost.

What the boundary frame actually is, from the WebKit record tree of one 45.3 ms frame:

```
45.3ms  FireAnimationFrame
  45.2ms  FunctionCall        (self ~10ms)
    21.3ms  RecalculateStyles
    13.6ms  Layout
```

A style-and-layout pass driven from JS, not Monaco's rendering. It lands on exactly one frame per crossing:
down-crossings at a fixed +150 px past the boundary, up-crossings at -550 px before it.

**The frame that stalls is the frame in which the set of rendered sections changes.** Sampling every rendered
`.unified-review-file` per frame (from `style`, so sampling forces no layout): 24/24 bursts agree — where the
section set changes, that frame is 45-83 ms; where it does not, the worst frame in the burst is 11-29 ms. On a
30-file traversal (1206 frames, 21 stalls >=30 ms): 9 stalls carry a section insertion, 7 a removal, 5 neither —
and of **1175 calm frames, none contains an insertion or a removal**. `monaco.editor.create`/`dispose` never land
in a stall frame, so construction is not the cost; the DOM/style work around it is.

Confirmed by ablation: preventing set changes outright (`overscan: 6` on a fixture with only 6 rows, so nothing
ever unmounts) removes the whole tail.

| | shipped | no set changes |
|---|---|---|
| boundary p99 / max | ~50 / 82 ms | **16 / 18 ms** |
| mid-file p99 / max | ~49 / 66 ms | 15 / 24 ms |
| traverse p99 / max | — | **13 / 21 ms** |
| spanning-boundary p50 | 18-33 ms | **13 ms** |
| frames over 40 ms | 47-51 | **0** |

That is an ablation, not a fix — it just keeps every section mounted. Two candidate fixes were measured and
**rejected**:

- **Per-frame `aria-valuenow`/`aria-valuemax` writes on the review scrollbar.** Plausible on Linux, where at-spi
  is live and an attribute change on `role=scrollbar` posts an accessibility notification. Ablated: frames over
  40 ms 47 -> 51. Not the cause.
- **Retaining a departing section until the review holds still** (released via a `whenStill` frame pump, unioned
  in document order so the list never reorders). Traverse p50 improved 7 -> 5 ms but the tail got **worse**:
  p99 39 -> 47 ms, max 57 -> 87 ms, frames over 40 ms 12 -> 17. Deferring removals does not help while
  insertions remain, and retained sections add per-frame work.

Still open: making a section enter and leave the rendered set without a full style-and-layout pass. Deferring the
*mount* is the untried half, and it previously broke reveal-based navigation because a reveal is itself a scroll
(see the note at the end of this document) — so it needs navigation to force a mount rather than wait for
stillness.

## What the boundary frame was: a forced synchronous layout

The stall is a **forced synchronous layout**, and the record tree proves it: `RecalculateStyles` and `Layout` are
*children* of the rAF's `FunctionCall`, not siblings, so they ran inside the JS call. In WebKit that only happens
when script reads geometry while layout is dirty. Two sites on the review's own path did exactly that, both firing
when a section's box or offset changes — which is the boundary frame:

1. `ReviewFileSection`'s ResizeObserver read `clientTop` / `clientHeight` / `offsetHeight`, **wrote** the sticky
   header transform, then called the editor's `layout()`, which reads geometry again.
2. The section's offset effect wrote `article.style.top` and then called `layout()`, which reads *because of* that
   write, so it could not simply be reordered.

The first is fixed by ordering every read ahead of every write. The second by shifting the band by the offset
delta: moving a section moves every descendant with it, so the container's offset changes by exactly that amount —
which holds regardless of which notices are rendered above the editor and of the section's box model. A
`containerTop` derived from header height would **not** hold, because `ReviewFileBody` renders
`.unified-review-notice` siblings above the editor. Structural changes still re-measure through the observer, and
`.unified-review-file` is content-driven (`position: absolute`, no height), so a notice appearing resizes the
article and triggers that re-measure.

Measured on the 30-file fixture (`CROSSING_FILES=30 CROSSING_LINES=600 CROSSING_DELETED=6 CROSSING_HUNK_STRIDE=30`),
whole-review traversal of 1206 moving frames, and 24 boundary bursts:

| | before | after |
|---|---|---|
| traverse p50 | 7 ms | **4 ms** |
| traverse p95 / p99 | 12 / 39 ms | 9 / **29 ms** |
| traverse max | 57 ms | **38 ms** |
| traverse frames over 40 ms | 12 | **0** |
| burst frames over 40 ms | 9 | **0** |
| burst worst frame (up / down) | 60 / 53 ms | **39 / 30 ms** |

So p50 4 ms clears 240 fps at the median and every frame over 40 ms is gone, but **p99 29 ms does not meet the
under-10 ms goal**: 29 of 1206 frames still exceed 16 ms, and p99 under 10 ms allows at most 11 over 10 ms. The
remaining forced reads are on the *insertion* path — `measureElement`'s uncached branch and `resizeItem`, both
`getBoundingClientRect` — and the estimate error below makes every measurement correction large.

## The controls that matter

| arm | p50 | p90 | p95 | p99 | max |
|---|---|---|---|---|---|
| idle, review open, nothing moving | 4 | 5 | 5 | 6 | 12 |
| **ordinary editor pane**, same file, same wheel | **8** | **13** | **13** | 16 | 16 |
| review mid-file | 8 | 9 | 10 | 28 | 31 |
| review boundary burst | 5 | 9 | 9 | 30 | 47 |
| the exact frame spanning a file boundary | 23 | 29 | 29 | — | 29 |

Two conclusions worth keeping:

1. **Idle rAF is 4 ms**, so 240 Hz is genuinely available — the host already disables WebKit's
   `PreferPageRenderingUpdatesNear60Fps` (`WebKit.EnableNativeRefreshRate`). Pacing is not the limit.
2. **The ordinary editor is as slow as the review mid-file.** Scrolling *any* Monaco editor in this WebKitGTK
   build costs ~8 ms/frame. The ~8 ms floor is Monaco plus WebKit rasterisation, shared by every editor in the
   app; what is genuinely review-specific is the boundary spike.

Frame times land on vsync multiples (8.3 = 2 periods, 12.5 = 3, 16.7 = 4), so this is dropped frames from
overshooting a 4.17 ms budget by a small margin, not continuous work.

## Where a frame goes

WebKit Timeline plus `FunctionCall` self-time, 40-notch mid-file burst (~46 moving frames, 208 ms of traced
main-thread work — about **1.5 ms/frame**, far below the 8 ms interval):

| ms | calls | site |
|---|---|---|
| 89.1 | 44 | Monaco's animation-frame runner — its own view render, ~2.0 ms per scroll tick |
| 10.9 | 40 | Monaco worker message plumbing (tokenisation), one per notch |
| 4.9 | 3 | Weavie spell-check `check()`, 1.6 ms each (visible-range only, so scroll must retrigger it) |
| 1.9 | 40 | review viewport wheel handler |
| 0.2 | 40 | **`UnifiedReview`'s scroll handler — 0.005 ms/frame** |

Layout 35 ms / Paint 30 ms / RecalculateStyles 26 ms across the burst. The review's per-frame bookkeeping is
measurably not a factor; the cost is Monaco's render plus WebKit style and raster.

## The boundary, and why only half of it is fixable cheaply

`review-editor-viewport.ts` sizes each section's Monaco band to the visible slice of that section. Mid-file the
band is constantly `viewport.height`, so no layout — but a section entering from below grows
(`viewport.height + viewport.top`) and one leaving shrinks (`contentHeight - top`), each forcing
`editor.layout(dimension, true)` every frame. The windows overlap for a full viewport height of scrolling, so a
boundary costs ~30 consecutive frames of two forced layouts. **Those layouts cost 0.5 ms each and are not the
boundary's cost — see the correction above before using anything in this section.**

A **constant** band height removes all of it — measured 283 forced layouts to 10 over a five-boundary
traversal — but it is not shippable. Holding height `H` requires clamping position into
`[0, contentHeight - H]`, which removes the last `H` pixels from Monaco's own scroll range, and that range is
the channel `onDidScrollChange` uses to carry Find / Go to Line / same-file definition reveals into the review.
`unified-review-input.spec.ts` "partially visible small file" fails on it. Compensating with a cursor-driven
reveal made things worse (distant Find, keyboard navigation across 5,000 lines, bounded allocations, wrong-file
navigation in a large review set).

What ships instead: quantise the band only while a section **grows into view from below** (`viewport.top < 0`),
where `top` is pinned at content row 0 regardless of height, so `top` and Monaco's scroll range are unchanged.
Every other position keeps the exact previous computation. The leaving half still resizes per frame, because
shrinking the band without advancing `top` would leave the section's tail unrendered and advancing `top` is the
clamp that breaks reveal.

## Removed-line ghosts rewrote every parked zone, every frame

Monaco calls each view zone's `onDomNodeTop` during its own render pass, so work there is charged to Monaco's
render and never appears as a separate Timeline record. `diff-zones.ts` windows a ghost's rows from the zone's
top, and Monaco parks an invisible zone at a large negative top — so the computed start row ran past the
ghost's last row and kept climbing, the `start === renderedStart` early-out never matched, and every parked
zone rewrote its `transform` and `textContent` on every render.

Measured on a review shaped like a real one (a six-line deletion every thirty lines across three 1,200-line
files, 123 ghost zones, one 400-notch traversal):

| | before | after |
|---|---:|---:|
| frames sampled | 939 | 940 |
| frames performing ghost row writes | **861 (92%)** | **134 (14%)** |
| `textContent` replacements | **33,018** | **216** |
| mean per frame | ~35 | **0.23** |

The remaining writes are a ghost the view is genuinely scrolling through. Settling on one empty write — and
leaving the transform alone — is what makes it stable; an earlier attempt clamped the start row to the line
count, which changed the transform for far-offscreen zones and broke `diff-review.spec.ts` "renders and reviews
a 5,000-line rewrite". `unified-review-scroll.spec.ts` now guards the invariant and fails loudly without the fix
(3,214 writes against an 80 threshold).

**This also corrects the profile above.** The 89 ms of `FunctionCall` self-time attributed to Monaco's
animation-frame runner includes this work, because a plain callback invoked inside Monaco's render produces no
child record. Every fixture used for the native timing runs was built from wholly-new files, which produce no
removed-line ghosts at all, so that code path was switched off in exactly the measurements used to conclude the
floor was Monaco's own render. How much of the ~8 ms it accounts for on WebKitGTK is unmeasured — the display
locked before a native arm could run.

## What the floor is made of

Toggling one editor setting at a time through the real settings path and reading CDP's cumulative main-thread
counters across an identical 120-notch burst (three edited 1,200-line files). Headless Chrome cannot show jank,
so these rank features by work, not by frame time — but the ranking is what transfers.

| setting disabled | TaskDuration | saving | ScriptDuration |
|---|---:|---:|---:|
| — (all on) | 1358 ms | — | 270 ms |
| `editor.bracketPairColorization` | 1339 ms | 18 ms (1%) | 235 ms |
| `editor.renderWhitespace` | 1297 ms | 61 ms (4%) | 224 ms |
| `editor.inlayHints` | 1296 ms | 62 ms (5%) | 225 ms |
| `editor.stickyScroll` | 1284 ms | 74 ms (5%) | 207 ms |
| `editor.indentGuides` | 1265 ms | 93 ms (7%) | 200 ms |
| **`editor.smoothScrolling`** | **1058 ms** | **299 ms (22%)** | **151 ms** |

**Smooth scrolling dominates, by about 3x the next feature.** The review's scroll owner
(`review-scroll.ts`, `SMOOTH_SCROLL_MS = 125`) interpolates each wheel notch over 125 ms, so the scroll position
changes on *every* frame — forcing `sync()` on every mounted section plus a Monaco `setScrollTop` and render
each time. With it off a notch resolves in one frame rather than roughly eight. Monaco's own smooth scrolling is
already disabled for review editors (`createReviewEditor` passes `smoothScrolling: false`), so this is Weavie's
animation, not Monaco's.

That reframes the 240 Hz question: the per-frame budget is tight in large part *because* smooth scrolling asks
for a render on every frame. It is a user-facing preference, so the tradeoff — visibly smooth interpolation
against roughly a fifth of the main-thread scroll cost — belongs to the user rather than to this change.
Worth confirming natively once a display is available; the ranking here is Blink.

## Native before/after, measured

Same isolated focus-protected instance, display awake, one run per arm. Two fixtures: `edit` (a hunk every 120
lines, one removed row each) and a ghost-heavy one (a hunk every 30 lines, six removed rows each).

`edit`, one removed row per hunk:

| metric | before | after |
|---|---|---|
| idle p50 / p99 | 4 / 7 | 4 / 8 |
| ordinary editor pane p50 / p99 | 12 / 35 | 12 / 30 |
| whole traversal p50 / p99 | 8 / 21 | 8 / 24 |
| mid-file p50 / p99 | 7 / 36 | 7 / 38 |
| boundary p50 / p99 | 8 / 41 | 8 / 44 |
| **frame spanning a boundary, p50** | **33** | **18** |
| boundary forced layouts | 254 | 158 |

Ghost-heavy, six removed rows per hunk:

| metric | before | after |
|---|---|---|
| **mid-file median p50** | **8** | **5** |
| mid-file forced layouts | 101 | 62 |
| boundary median p50 / p99 | 8 / 39 | 8 / 38 |
| boundary forced layouts | 315 | 192 |
| whole traversal p95 / p99 | 9 / 13 | 12 / 16 |

Supported: the boundary-spanning frame roughly halves (33 → 18 ms), forced layouts fall about 40% in both
motions, and mid-file p50 improves 8 → 5 ms once ghosts are multi-row.

Not supported: **p99 under 10 ms.** Burst p99 stays at 35-44 ms in both arms and p50 at 7-8 ms outside the
ghost-heavy mid-file case; the single-run p99 differences sit inside run-to-run noise. The ordinary editor pane
measures p99 30-35 ms on the same machine, which places the residual tail outside the review.

## The tail is three different things, and it decides the p99 question

`p50` and `p99` are separate problems and conflating them leads to the wrong conclusion. On a whole-review
traversal (945 moving frames, untraced): **p50 8 ms, 34 frames over 10 ms, 10 over 16 ms, 3 over 40 ms.** So the
8 ms is the floor and the tail is ~2% of frames. p99 under 10 ms does **not** require 240 fps — with p50 at 8 ms
it only requires that at most 9 frames exceed 10 ms.

Timing every `monaco.editor.create` and correlating against the frames that overran shows construction itself is
cheap — **4-5 ms** — while the frames containing it run 62-65 ms. Disposal is 1-2 ms inside 29-39 ms frames. So
mounting is implicated but `editor.create` is not the cost. On the later 30-file traversal, create and dispose do
not land in a stall frame at all (see the correction at the top): what they accompany is a change to the rendered
section set, which is the thing that costs.

A traced traversal attributes the worst frames by record self-time (tracing inflates cadence, so it is used for
attribution only):

| class | evidence | cause |
|---|---|---|
| **A. section mount** | 95 ms and 85 ms frames with `FunctionCall` self-time 58.6 / 50.5 ms | ~55 ms of JS *around* `editor.create`: `connectTextEditor` (symbol source, git blame, spell check), `createInlineDiff`, decoration configure, `prepareGeometry` |
| **B. style storms** | 46 / 34 / 31 / 27 ms frames with `RecalculateStyles` 9-12 ms and `Layout` 5-8 ms, small JS | style and layout recalculation |
| **C. off main thread** | 106 ms and 92 ms frames with only ~13-16 ms of accounted records | raster, compositing or GC — nothing the main-thread timeline records |

**Why p99 under 10 ms is not reachable from the review:** 34 frames exceed 10 ms and the budget allows 9. Mount
and dispose frames are only about 6 of the 34. Closing the gap needs all three classes, and class C is not
main-thread work at all. The ordinary editor pane measures p99 30-35 ms on the same machine, so class B and C
are not review-specific either.

**Class A was attempted and reverted.** Constructing a section's editor only once the review scroll has held
still for a frame (a `ReviewScroll.idle()` signal gating the work in `ReviewFileBody`) moves the ~55 ms off
moving frames, and unit tests stay green — but it breaks `unified-review-history.spec.ts` "same-file definition
opens the file and restores the review departure in both directions" deterministically, 2/2 in isolation, with
"Review is not mounted".

The reason is structural rather than incidental: a reveal *is* a scroll, so gating the mount on scroll-idle
defers it exactly when navigation needs the target section mounted in order to restore a location. `settle()`
waits for `sections.set`, which now arrives only after the gesture ends. Any workable version has to exempt
navigation-driven mounts from the deferral, which is more surface than the measured benefit justifies — the
change does not reach p99 under 10 ms on its own, since mounts are about 6 of the 34 frames over 10 ms.

**The original description of the lever:** class A is addressable by constructing a section's editor only
while the review scroll is idle, rather than in whatever task follows the working-copy open. That moves ~55 ms
of JS off moving frames entirely and would cut burst p99 substantially, since one mount dominates a 26-frame
burst. It is deliberately not in this change: it alters when a section's content appears, which is a UX
behaviour change that wants validating against the full suite and a human eye, not a 2 a.m. commit. It is not a
route to p99 under 10 ms on its own.

## Open, and deliberately not bundled here

- **The ~8 ms floor.** Shared with the ordinary editor, so closing it means Monaco's per-frame render or
  WebKit's raster cost app-wide. Larger than a review scrolling change.
- **Section height estimates use a hardcoded line height.** `review-context.ts` hardcodes 19 — Monaco's value
  for font size 14, while Weavie defaults to 16 and derives `round(fontSize * ratio)` with ratio 1.35 off macOS
  and **1.5 on macOS**: 22 and **24**. So every unmounted section reserves 14% too little room off Mac and
  **21% on it**, and TanStack applies the estimate-to-actual delta as a scroll adjustment when a first-measured
  section sits above the fold. Correcting it shifts virtualiser geometry enough to add one model attach in
  `unified-review-measurement.spec.ts`, so it needs its own change that handles that.
- **`diff-zones.ts` ghost start row is unbounded.** Monaco parks an invisible zone at a large negative top, so
  the computed start exceeds the ghost's line count and the early-out never stabilises. Bounding it breaks
  `diff-review.spec.ts` "renders and reviews a 5,000-line rewrite", which passes on main — the transform change
  for a far-offscreen zone perturbs the layout that spec measures. Needs a fixture that exercises multi-line
  ghosts.

## Reproducing

Native harness and page probe: `temp/diff-scroll-profile/crossing-native.mjs` and `crossing-probe.js` (both
temporary, gitignored). One owned focus-protected instance via the scoped KWin rule; trusted input is
page-local only. **Under a locked display the NVIDIA EGL compositor path segfaults the WebProcess** — six
coredumps inside `libnvidia-eglcore`, reproducing identically on unmodified main — so native runs need the
display awake. Work-count measurements run on the deterministic headless harness instead
(`temp/diff-scroll-profile/probes/`); note that headless Chrome paces rAF at a fixed 16.7 ms and never drops a
frame, so it measures work, never jank, and the host serves a `wwwroot` baked at build time — `vite build`
alone is not enough.
