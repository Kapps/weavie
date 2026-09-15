# Transcript scrolling

The transcript viewport owns position, unfinished motion, measured row geometry, and mounted row
identity. The browser does not own a second vertical scroll position. The pane supplies input and
whether the user is following the latest output.

## Root cause and required contract

Issue [#886](https://github.com/Kapps/weavie/issues/886) exposed inappropriate smoothing of precision
trackpad input. Repeated small deltas must retain their exact distance and must not acquire a new
animation tail. Physical wheel notches may animate according to the effective editor setting.

Dynamic virtualization adds a separate problem: an estimated row height can change while scrolling.
Changing the visible offset without translating the unfinished motion consumes or repeats part of
the user's input. Letting native scrolling, virtualizer corrections, and follow-latest writes own
position independently requires timing and offset-matching rules to distinguish their effects.

A geometry update must commit dimensions, current position, and remaining motion together. If a row
above the reading anchor grows by 80px, both the current offset and unfinished target move by 80px.
The easing curve and deadline stay unchanged. Clamping at a boundary cannot revive already-consumed
motion when content subsequently grows.

The reading anchor must come from the previously visible content, before incoming rows mount. Choosing
an incoming estimated row instead loses the part of its height correction below its own top. The
browser regression exposed a 10.6875px error across three immediate 120px wheel events. Measurement
must also follow the translated target range, so a large jump finishes with the actual viewport mounted.

## Ownership

`AgentTranscriptViewport` renders Solid rows through the bundled Monaco `ListView`. The dependency
patch provides atomic geometry updates and keyed row reconciliation. `Scrollable` owns the motion
curve; `ListView` owns the height map, reading anchor, focus retention, and row lifecycle.

Geometry events preserve the pane's current following policy. Position events identify navigation or
input. Explicit input publishes intent even at an unchanged clamped position.
An active input animation pauses following; settled input near the bottom resumes it. Explicit
jump commands set the desired following policy for their synchronous navigation operation.

The viewport uses fractional coordinates throughout, including row measurements and half-open
visible ranges. Pending forms and the focused row remain mounted outside the visible range. A keyed
update renders and measures surviving mounted rows before releasing retention; resolving an offscreen
form therefore cannot leave its previous expanded height in the height map.
Focus retention only changes which existing rows remain mounted; it does not request layout or
follow-latest navigation. A browser regression caught an unchanged-geometry focus pass moving a button
58.859375px between mouse-down and mouse-up, causing its click to land on the transcript background.

Solid data reconciliation runs at its DOM commit boundary, outside the current reactive update batch.
This lets the synchronous list renderer measure the DOM produced by its setters. This scheduling is
confined to data changes; scroll motion and geometry updates do not wait for an application timer.

Hidden panes preserve their last measurable geometry. Data updates coalesce until the pane becomes
visible; a hidden row's zero bounding box is not a content-height measurement. The list owns its DOM
mount exclusively, with the empty-state overlay rendered in a separate Solid-owned slot.

Touch ownership is chosen once per gesture. Native taps, selection, pinch, and nested scrollable
controls remain native; a claimed vertical transcript gesture keeps the same owner through inertia.
Keyboard navigation and shared middle-click
autoscroll target the same viewport model as wheel input. Session snapshots preserve the reading row
key and intra-row offset, rather than assuming a raw offset survives a width change.

## Why not remove virtualization or use native anchoring alone?

The investigation tested both. In the WebKit 26.5 cold-history prototype, a 120px wheel notch lost
up to 101.6875px of visible movement while estimated heights changed. Fully measured rows and normal
flow controls moved exactly 120px. A separate minimal reproduction showed that a layout mutation plus
a synchronous geometry read inside a scroll handler could leave later offscreen growth unanchored;
mutation-only, read-only, and frame-deferred-read controls stayed anchored. Deferring reads fixed that
minimal case but did not eliminate the cold-virtualization error.

Rendering all 5,000 turns avoided estimation, but one ordered comparison measured 435MiB after GC
and 586,338 DOM nodes, versus 57MiB and 1,706 nodes for the virtualized baseline. These are diagnostic
samples, not general performance guarantees. The chosen design keeps bounded rendering and makes
motion/geometry ownership explicit instead of relying on browser anchoring timing.

## Verification

- `src/web/src/agent/scroll-geometry.test.ts` exercises the installed motion model: fractional
  coordinates, atomic translation, clipping, unchanged deadlines, and reentrant completion.
- `src/web/src/agent/AgentTranscriptInput.test.ts` checks keyboard ownership and middle-click wiring.
- `src/web/e2e/agent-scroll-smoothness.spec.ts` measures actual rendered anchors through cold history,
  wheel bursts, and fractional input. The sampler must be ready before input is injected.
- `src/web/e2e/agent-composer.spec.ts` covers pending forms, reading position, following, navigation,
  and session switching. Geometry assertions read the rendered viewport rather than native
  `Element.scrollTop`.
- Full-host history checks traverse the viewport with user input, verifying complete ordered row
  identities, restored media, and independent side conversations across unmounting and session switches.
- `src/web/e2e/middle-click-autoscroll.spec.ts` covers the shared gesture and nested surface ownership.

Browser recordings and these end-to-end checks are required before shipping. Numerical model tests
cannot establish DOM alignment or physical Mac trackpad behavior by themselves.
