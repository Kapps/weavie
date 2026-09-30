# Unified review scroll cost: where the frame time actually goes

Measured on the real Linux GTK4/WebKitGTK desktop build (RTX 4090, 3840x2160 @ 240 Hz, logical 2560x1440,
content viewport 1280x840 at DPR 2), in one isolated benchmark instance, five agent-applied 5,000-line files,
page-dispatched wheel at one notch per animation frame.

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
boundary costs ~30 consecutive frames of two forced layouts.

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
