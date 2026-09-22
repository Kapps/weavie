# Unified review scroll rendering

The unified review uses one logical scroll position for its file list, pinned headers, and bounded
Monaco viewports. Their placement uses CSS transforms. Completion, hover, and rename widgets live
outside the transformed content so their fixed positions remain relative to the window.

Each review section owns an external widget layer. The Monaco patch gives every view using that
layer its own child container, focus tracking, inherited context keys, and keyboard registration.
Nested definition peeks share the placement layer but own separate children. View disposal removes
those children; section disposal removes the layer. This keeps popup keyboard commands attached to
the editor that opened them, including when a peek remains mounted.

## Investigation

The desktop engines are WebKitGTK on Linux and WKWebView on macOS; the Firefox web client uses Gecko.
Both desktop hosts already configure native-refresh rendering. The review's scroll path moved the
list, pinned header, and editor viewport with `top` on every animation frame. Monaco's subsequent
geometry reads forced the browser to lay out the surrounding document before rendering text.

A Chromium trace over 180 scroll frames recorded 540 layout passes. With transform placement it
recorded 387, about 28% fewer. Forced layouts attributed to Monaco's `onBeforeRender` fell from 180
to 27. Remaining work includes Monaco text measurements, normal rendering, and geometry changes;
transforms do not make an editable, virtualized diff independent of the main thread.

## Native Linux measurements

Measured on 2026-09-22 using the actual Linux application and native bridge, WebKitGTK 2.52.6,
GTK 4.18.6, Mesa 25.0.7, and a 60 Hz Xvfb display. The isolated workspace contained five added
TypeScript files of 5,000 lines each. Each run started at the same position and delivered 360 real
X11 wheel events over approximately 5.8 seconds. Sampling read scroll styles and `textContent`,
without forcing layout. The second pair reversed the execution order.

| Run | Animation updates/second | Median frame interval | Intervals over 25 ms |
| --- | ---: | ---: | ---: |
| Original, pair 1 | 38.2 | 25 ms | 48.4% |
| Transform placement, pair 1 | 45.1 | 19 ms | 26.7% |
| Transform placement, pair 2 | 46.2 | 19 ms | 24.3% |
| Original, pair 2 | 40.9 | 23 ms | 38.8% |

This is a 13–18% increase in animation updates in the native test environment. It is evidence of
reduced rendering cost, not a physical-display frame-rate guarantee. Xvfb uses software rendering;
the user's GPU, Wayland compositor, high-refresh display, and macOS were not measured here.

A real mouse notch produced pixel-mode input with `wheelDeltaY = -120` and animated across several
frames to its 50-pixel destination. Mouse-wheel misclassification was therefore not the cause in
this native reproduction. The change retains Monaco's existing wheel handling and smooth-scroll
setting.

## Regression coverage

The unified-review journeys cover bounded painting, fractional scrolling, file remounts, pinned
headers, resize, keyboard navigation, Find, review actions, and autoscroll. The widget journey checks
completion positioning after scrolling and changing files, click acceptance, editor focus, and
cleanup. It also accepts and cancels rename with a definition peek open: sharing popup focus or
losing its keyboard context makes Escape fail in that scenario.
