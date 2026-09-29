# Unified review scroll rendering

Unified review retains each file's prepared presentation and activates a Monaco editor for input.
Crossing a file boundary does not construct an editor, calculate a diff, or remeasure its text.
One logical scroll position places sections, pinned headers, and the active editor using cached
geometry and top offsets. Monaco still controls its own text rendering when editing.

Each review section owns an external widget layer. The Monaco patch gives every view using that
layer its own child container, focus tracking, inherited context keys, and keyboard registration.
Nested definition peeks share the placement layer but own separate children. View disposal removes
those children; section disposal removes the layer. This keeps popup keyboard commands attached to
the editor that opened them, including when a peek remains mounted.

## Retained presentation

Working-copy references, versioned diff geometry, guarded review actions, comments, and horizontal
positions belong to the review tab and file, independently of either paint adapter. Passive and live
presentations share those owners; switching presentation cannot dispose the working model or replace
another adapter's comment placement owner.

File resources, worker diffs, and tokenization prepare independently. Only synchronous projection and
paint construction use a serialized queue, yielding between files. Queued paint validates model
version, language, source identity, and visual revision before publishing.

Prepared text uses Monaco's line projection, tokens, injected text, font, and wrapping rules. Four-row
paint chunks are retained but only the visible band is attached. Focused or selected chunks remain
attached. This is bounded DOM painting, not a pool of offscreen interactive editors or a reliance on
`content-visibility` skipping Monaco's JavaScript work.

Activation requires a current prepared projection and identical height. Offscreen unfocused editors
are parked without reconstructing them on scroll. Handing editing to another file retires the previous
editor after its input, composition, and widget focus release. Horizontal position has one measured
range authority: prepared text width survives layout-only resize, while text, token, decoration, font,
and wrapping changes invalidate it. Hidden passive geometry cannot clamp a live editor's position.

Preparation failures remain visible and retry through explicit file navigation. Tab close releases
subscriptions, paint ownership, temporary worker sources, and working-copy references. There are no
production experiment flags or graphics configuration changes.

## Interaction ownership

A section registers once for its file lifetime. Its reading location remains capturable while its
presentation is pending, unavailable, collapsed, or empty; registration does not grant permission to
restore or focus an editor. Availability getters only observe state. Preparation and activation own
invalidation and publication, so reading toolbar state cannot restart preparation.

The surface owns one cancellable navigation request through activation, placement, and focus. A ready
section grants a capability for the exact current live adapter, activating passive paint before any
cursor-dependent restoration. The adaptive owner commits live visibility before granting that
capability. Ambient pane focus joins an unfinished navigation instead of replacing its destination.
Without a navigation request, pane focus uses an existing live input or the outer scroller; opening
the review does not activate an editor. Keyboard or pointer entry into a file grants editing ownership.
User input, replacement, and disposal revoke pending requests. State
notifications from a retired binding cannot affect its replacement. An existing live editor remains
focusable while diff geometry is pending, but hunk navigation waits for current geometry.

Availability notifications settle navigation and refresh controls; they do not persist a new reading
location. Scroll, cursor, horizontal-position, and tab-lifecycle changes own location persistence.

Explicit navigation expands a collapsed file. Restoring a collapsed tab preserves its saved outer
position without restoring or focusing hidden content. Preparation failures retry only for a new
explicit request; there is no independent background activation scheduler.

## Measured behavior

The paired timing build has asset SHA256
`8f66cf6cbab3265728c75767539d3060e47d8716fb2fbabc80e3d0f4e273edab`; these numbers precede
the interaction-ownership corrections and are not a timing measurement of the final PR build.

The September 28, 2026 production-candidate comparison used the same compiled assets, 105-file Git
comparison, settings, 1280×840 CSS viewport, and DPR 2 in the native Linux app and headed Chrome.
The native host was a Debug 0.1.0.0 build with temporary measurement and non-focusable-window probes.
Hardware was an RTX 4090, NVIDIA 615.71.09, KWin 6.7.5 Wayland, and a 3840×2160 display at 240.023 Hz.
The native engine was WebKitGTK 2.52.6 / GTK 4.22.5; Chrome was 151.0.7922.137. Smooth scrolling was
enabled. Both windows remained behind the user's applications; sandboxing and graphics settings
were unchanged.

Each 15-second traversal dragged the outer scrollbar through its actual event handlers, visiting the
whole list forward, backward, and twice again. The first traversal began after initial preparation,
not while files were loading. All 104 boundaries were crossed on each pass.

| Measurement | Native WebKitGTK | Chrome |
| --- | ---: | ---: |
| Review surface ready | 244 ms | 301 ms |
| All 105 files prepared | 2.62 s | 2.29 s |
| First traversal animation-interval p99 | 6 ms | 4.3 ms |
| Return/repeat animation-interval p99 | 5 ms | 4.3 ms |
| File-boundary animation-interval p99 | 5–9 ms | 4.3 ms |
| Process-tree CPU per 15-second traversal | 16.62–17.30 s | 13.62–13.98 s |
| Editor creates/disposals and diff-worker calls during traversal | 0 | 0 |

Native fresh-buffer presentation feedback measured p99 4.187–4.203 ms. Two approximately 91.7 ms
presentation gaps remained. Buffer presentation is not proof that text changes on every frame;
browser animation callbacks are not physical presentation measurements either. Concurrent user GPU
activity is a remaining confounder, and these results do not establish macOS WKWebView performance
or physical touchpad behavior.

Retaining prepared paint trades memory and initial preparation for cheaper traversal. Whole-process
PSS increased from 642 to 1,831 MiB in WebKitGTK and 590 to 1,278 MiB in Chrome on this fixture.
Closing returned the working-model count from 105 to the two previously open models, but process
memory did not return to baseline. That model-count check alone does not prove total memory
reclamation. No forced collection, memory cap, or graphics workaround is part of the renderer.

## Regression coverage

The unified-review journeys cover first/return traversal without editor activation, fractional
scrolling, pinned headers, parked resize and horizontal restoration, editing/save, review actions,
comments, empty files, EOF deletions, RTL, wrapping, injected text, and autoscroll. The widget journey
checks completion positioning after scrolling and changing files, click acceptance, editor focus,
and cleanup. It also accepts and cancels rename with a definition peek open: sharing popup focus or
losing its keyboard context makes Escape fail in that scenario.
