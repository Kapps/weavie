# Unified title bar — review

Reviews [PR #1253](https://github.com/Kapps/weavie/pull/1253) against [design.md](design.md).

- **Baseline:** `git diff 31359366 62f7e0e9` (branch point on `main` to the PR head; merged as `9faf4783`).
- **Recheck baselines:** `git diff b10f734e 4c9168de` (first fix commit) and `git diff 4c9168de de794595`
  (second fix commit), both on [PR #1258](https://github.com/Kapps/weavie/pull/1258), on `main` at `b10f734e`.
- **Method:** one `weavie-reviewer` pass over the design, the code diff and the verification record,
  covering correctness, design conformance, repo standards, duplication and tests. Each finding was then
  checked against the code; R1, R2 and R9 were also measured. The recheck read the fix commit against each
  finding, rebuilt the page, reran the specs and unit tests, and measured the bar across layouts and widths.
- **Result:** 2 bugs, 7 warnings, 11 nits in the review, plus 2 warnings and 1 nit found in rechecks. 18 are
  confirmed fixed and none reopened. Open: the nits R10, R15, R20 and R23, and R19, which needs the macOS
  hand test.
- **Dropped:** "the window tests sit in a class named for terminal actions" — they were there before this
  PR.

| # | Severity | Status | Finding |
| --- | --- | --- | --- |
| R1 | bug | fixed | A Linux or macOS window 760px wide or less has no window buttons and nowhere to drag |
| R2 | bug | fixed | On X11 without a compositor the Linux window is 10px smaller after every relaunch |
| R3 | warn | fixed | Double-click does nothing when the desktop's action is `toggle-maximize-horizontally` or `-vertically` |
| R4 | warn | fixed | The press-pairing rule exists twice; the macOS copy has no test |
| R5 | warn | fixed | Two new lint suppressions were added without asking |
| R6 | warn | fixed | The real-app drag check is timed by fixed sleeps |
| R7 | warn | fixed | Fixtures, label tables and chrome rows are duplicated |
| R8 | warn | fixed | The design promises checks and edits the PR does not contain, and contradicts itself on the hello push |
| R9 | warn | fixed | The search box is off-centre in a band of window widths, including 1280px with left-side buttons |
| R10 | nit | open | `window.control {}` minimizes the window; `window.resize {}` resizes from the left |
| R11 | nit | fixed | `stopPropagation` in the caption handler cannot stop autoscroll |
| R12 | nit | fixed | A triple-click runs the double-click action twice |
| R13 | nit | fixed | Pressing the Weavie logo on Linux does not drag the window |
| R14 | nit | fixed | The welcome strip undoes `.titlebar`'s grid by import order, sets unused insets, and never dims |
| R15 | nit | open | Two hosting tests are heavier or weaker than they need to be |
| R16 | nit | fixed | Three comments run past two lines |
| R17 | nit | fixed | On Windows the build number is now in the drag region and may have lost its tooltip |
| R18 | nit | fixed | `publishWindowControl` lives in `caption-press.ts` |
| R19 | nit | open, unverified | Two macOS behaviours to check by hand: a long drag, and the toolbar in fullscreen |
| R20 | nit | open | In a right-to-left desktop GTK's "start" side is the right; the page draws it on the left |
| R21 | warn | fixed | Found in recheck: with left-side buttons the search box shrinks to nothing between 760px and about 950px |
| R22 | warn | fixed | Found in recheck: in a narrow window the right end of the bar, window buttons included, is pushed off the window |
| R23 | nit | open | Found in second recheck: the width sweep restates the compact breakpoint as `760` |

## Bugs

### R1 — narrow desktop windows lose the bar

`src/web/src/mobile/mobile-commands.css:26`, `src/web/src/mobile/useCompactMode.ts:3`

The compact layout switches on by width alone (`max-width: 760px`) and hides `.tb-left` and
`.tb-controls`, with `.titlebar` set to `display: contents`. Before this PR the OS title bar stayed on
Linux and macOS, so that was harmless. Now the page's bar is the only one, and no host sets a minimum
window size.

- Measured in the built page with the Linux chrome at 720px: three window buttons in the DOM, none
  visible, and no `data-caption` element with a box.
- Linux: half-tiling on a 1366px or 1440px screen (683px, 720px) removes minimize, maximize, close and
  the drag area.
- macOS: the traffic lights stay but float over the compact layout, with no drag strip.

### R2 — saved size shrinks on X11 without a compositor

`src/Weavie.Linux/WorkspaceHost.cs:253` (saves), `src/Weavie.Linux/WorkspaceHost.cs:146` (restores),
caused by `src/Weavie.Linux/WorkspaceHost.Chrome.cs:52`

With the hidden title bar GTK draws a 5px solid border where there is no compositor.
`gtk_widget_get_width` / `_height`, which `CaptureWindowState` saves, exclude that border;
`gtk_window_set_default_size`, which `ApplyGeometry` restores with, includes it.

- A GTK 4.18.6 probe making the same three calls under Xvfb, with and without openbox: 800x600 becomes
  790x590, then 780x580, then 770x570.
- The same probe without the hidden title bar (the code before this PR) stays at 800x600.
- On Wayland (weston) the probe stays at 800x600. Composited X11 was not tested.
- `gtk_window_get_default_size` returned the requested size in every run.
- The design names this risk (`design.md:548`) and says the real-app spec reports the size on each
  launch. It does not.

## Warnings

### R3 — only the exact string `toggle-maximize` is handled

`src/Weavie.Linux/WorkspaceHost.Chrome.cs:108`

`design.md:392` specifies `"toggle-maximize*"`. GNOME also offers `toggle-maximize-horizontally` and
`toggle-maximize-vertically`; with either, a double-click on the bar does nothing.

### R4 — two implementations of the pairing rule

`src/Weavie.Mac/Hosting/WeavieWebView.cs:22`, `src/Weavie.Mac/WorkspaceWindow.Chrome.cs:28`,
`src/Weavie.Linux/Hosting/CaptionGesture.cs`

Linux has the pure, tested `CaptionGesture`. macOS restates the rule across `_press`, `Held`,
`TakePress()` and `PressCaption`, and nothing tests it: `TitleBarChecks` covers geometry, hit-testing and
first click only. The design chose this split; one gesture class in `Weavie.Hosting` would serve both.

### R5 — new lint suppressions

`src/web/src/chrome/TitleBar.tsx:29`, `src/web/src/welcome/WelcomeBar.tsx:11`

Both add `biome-ignore lint/a11y/noStaticElementInteractions`. Four older ones exist, but the standard is
to ask before suppressing.

### R6 — sleeps in the real-app drag check

`src/web/e2e/desktop/window-chrome.spec.ts:110` (also `:66`, `:113`)

After the page sees the press the spec sleeps 0.5s for the report to reach the host, then drags for ten
0.05s steps. A report that takes about a second arrives after the release and the window never moves,
which is the start-up delay commit `0edee1b0` addressed. Stepping the pointer until the window origin
changes removes the timing.

### R7 — duplication

- `LABELS` is typed in `src/web/e2e/titlebar.spec.ts:56` and
  `src/web/e2e/desktop/window-chrome.spec.ts:17`, both restating `label()` in
  `src/web/src/chrome/WindowControls.tsx:40`.
- The chrome rows of `src/web/e2e/titlebar.spec.ts:21` are retyped, with the whole
  `window.__WEAVIE_SHELL__` init script, in `src/web/e2e/bridge.spec.ts:70` and
  `src/web/e2e/middle-click-autoscroll.spec.ts:26`.
- The Windows row is typed in `src/Weavie.Win/WorkspaceWindow.cs:181` and again as `Frameless` in
  `tests/Weavie.Core.Tests/Shell/ShellTests.cs:9`.
- `tests/Weavie.Linux.Tests/LinuxTitleBarTests.cs:28` rebuilds the control wire names the enum attributes
  already own; the tests can compare `WindowControl[]` directly.
- `src/web/src/chrome/caption-press.ts:8` is a fifth lookup of the local host's `window` feature
  (`src/web/src/chrome/ResizeFrame.tsx:33`, `src/web/src/welcome/Welcome.tsx:17` and `:21`,
  `src/web/src/App.tsx:301`).
- `src/web/e2e/desktop/window-chrome.spec.ts:122` is the fourth hand-written report server in
  `e2e/desktop`, and `src/web/e2e/titlebar.spec.ts:10` another copy of the "built app not found" guard.

### R8 — design and code disagree

`docs/specs/unified-title-bar/design.md`

- `:546` says a browser test covers a middle press not starting autoscroll. The test checks only the
  `captionPress` payload (see R11).
- `:548` says the real-app spec reports the window size on each launch (see R2).
- `:534` lists an update to `docs/concepts/architecture-overview.md`. No doc outside this spec changed.
- `:325` says to check the build-number drag on Windows before changing it. No check is recorded (see
  R17).
- `:481` and `:509` say the chrome is pushed at hello; `:253` and the code say it is not.
- `:444` says fullscreen did-exit; the code observes will-exit
  (`src/Weavie.Mac/WorkspaceWindow.Chrome.cs:53`).
- `:381` says the recorded event is released on button-up; the code keeps it until the next press
  (`src/Weavie.Linux/WorkspaceHost.Chrome.cs:84`), as the late-report rule needs.

### R9 — the search box drifts off-centre before it narrows

`src/web/src/chrome/titlebar.css:13`

The grid centres the search box only while each side's content fits in half of what the 576px column
leaves. Below that the wider side keeps its content and pushes the box across.

- Measured in the built page: Linux chrome with menus and three left-side buttons at 1280px gives a 375px
  left side and a search box 23px right of centre. At 1440px it is centred.
- The browser spec's Linux row has two left-side buttons and passes.
- `576px` restates the search box's 560px (`src/web/src/chrome/omnibar.css:6`) plus `.tb-center`'s
  padding.

## Nits

- **R10** `src/Weavie.Hosting/WindowFeature.cs:70`. `WireJson` uses web defaults without required
  constructor parameters, so a missing or numeric field becomes the enum's first value. The deleted
  parser dropped these. From System.Text.Json's rules; not run.
- **R11** `src/web/src/chrome/caption-press.ts:23`. Autoscroll listens on `document` in the capture phase
  (`src/web/src/chrome/middle-click-autoscroll.ts:163`) and runs before this handler, which Solid
  delegates to `document`. The bar is safe only because it has no scrollable ancestor.
- **R12** `src/Weavie.Linux/Hosting/CaptionGesture.cs:70`, `src/Weavie.Mac/WorkspaceWindow.Chrome.cs:32`.
  `Count >= 2` also matches the third click, so a triple-click maximizes and restores. The design says
  the same.
- **R13** `src/web/src/chrome/TitleBar.tsx:41`. The press lands on the `<img>` inside `.tb-icon`
  (`src/web/src/chrome/WeavieIcon.tsx:7`), which carries no `data-caption`.
- **R14** `src/web/src/welcome/welcome.css:11`, `src/web/src/welcome/WelcomeBar.tsx:13`. `.welcome-bar
  { display: flex }` beats `.titlebar`'s grid only because `welcome.css` is imported second. `barStyle`
  sets inset variables the strip never reads. The strip never gets `.blurred`.
- **R15** `tests/Weavie.Hosting.Tests/HostCoreTerminalActionsTests.cs:145` boots a full host eight times
  to check eight enum names; `:142` asserts the test host's own default and cannot fail.
- **R16** `src/Weavie.Linux/WorkspaceHost.Chrome.cs:8`, `src/web/src/chrome/TitleBar.tsx:9`,
  `src/web/src/chrome/titlebar.css:3`.
- **R17** `src/web/src/chrome/titlebar.css:61`. `.tb-controls` no longer sets `app-region: no-drag`, so
  the build number is caption on Windows. WebView2 gives drag regions no mouse events, so its tooltip
  probably no longer shows. Needs a look on Windows.
- **R18** `src/web/src/chrome/caption-press.ts:33`.
- **R19** `src/Weavie.Mac/WorkspaceWindow.Chrome.cs:42`: if `PerformWindowDrag` returns only on
  mouse-up, a drag held past the bus's notice delay shows "Moving the window is taking longer than
  usual…". `src/Weavie.Mac/Hosting/MacWindowChrome.cs:30`: a toolbar usually stays visible in fullscreen
  and may cover the page's bar. Neither can be settled from the code; both belong in the macOS hand test.
- **R20** `src/Weavie.Linux/Hosting/DecorationLayout.cs:17`.

## Checked, no finding

- GTK, GDK and GLib signatures, the `GValue` layout, callback rooting and widget ownership.
- The boot chrome is rebuilt on every `index.html` request, so the recorded "chrome change during page
  load" gap does not survive a reload.
- Omnibar and menu dismissal use capture-phase `pointerdown`, so the caption handler's `preventDefault`
  does not break them.
- Weavie applies no page zoom on macOS, so the design's zoom risk needs no code.

## Fix stage

- **R1** fixed. A window without an OS title bar keeps its bar in the compact layout (`.titlebar.windowed`); only the omnibar folds. Browser test at 720px; fails on the old stylesheet.
- **R2** fixed. Saved size is `gtk_window_get_default_size`, the measure restore uses. GTK probe: default size tracks a user resize and restores unchanged. Real-app spec closes to welcome and reopens: passes, and fails on the old capture with 1270x830 → 1260x820.
- **R3** fixed. `toggle-maximize-horizontally` and `-vertically` toggle maximize; GTK 4 cannot maximize one axis.
- **R4** fixed. One `CaptionGesture` in `Weavie.Core.Shell`, in DOM button numbers, used by both hosts and tested in Core; the Linux drag threshold stays in the Linux host.
- **R5** fixed. Both suppressions removed: caption handling installs once per page on `document`, like middle-click autoscroll, so no static element carries a handler.
- **R6** fixed. No fixed sleeps: the page reports after two animation frames, the drag steps the pointer until the window origin changes, macOS polls for the app to be frontmost.
- **R7** fixed. Shared chrome rows and shell config (`e2e/harness/window-chrome.ts`), button names (`window-control-label.ts`), `WindowChrome.Frameless`, enum comparisons in the layout tests, `localWindowFeature()`, `localServer()` for all four desktop specs, and the built-app guard moved into `MockHost.start` (eight copies deleted).
- **R8** fixed. Design updated: page-level caption install, autoscroll reasoning, geometry from default size, shared gesture, will-exit, press kept until the next press, no hello push, no architecture-overview change.
- **R9** fixed. A `ResizeObserver` sets `--titlebar-side` to the wider side, so both columns are at least that wide; `--omnibar-width` replaces the restated 576px. Browser row with three left-side buttons at 1280px is centred; 23px off on the old stylesheet.
- **R10** open. Not touched by these fixes.
- **R11** fixed. `stopPropagation` removed; the browser test checks a middle press on the bar starts no autoscroll.
- **R12** fixed. A double-click is `Count == 2`; a third click is a plain press. Core test.
- **R13** fixed. `.tb-icon > *` has `pointer-events: none`.
- **R14** fixed. `.titlebar.welcome-bar` sets its own display; the strip sets only its height and dims with the window.
- **R15** open. Not touched by these fixes.
- **R16** fixed. The three comments are two lines.
- **R17** fixed. `.tb-build` is `no-drag` again, so Windows keeps the pre-PR badge and its tooltip.
- **R18** fixed. `WindowControls` publishes through `localWindowFeature()`; `caption-press.ts` holds only caption handling.
- **R19** open, unverified. Hand test on macOS.
- **R20** open. Not touched by these fixes.

## Recheck

Fix commit `4c9168de` (PR #1258), read against each finding and run locally. Nothing was fixed in this stage.

| Check | Result |
| --- | --- |
| `npx vite build` on `4c9168de` | succeeds |
| `playwright test e2e/titlebar.spec.ts e2e/middle-click-autoscroll.spec.ts e2e/bridge.spec.ts --project chromium` | 41/41 |
| `dotnet test tests/Weavie.Core.Tests --filter "Shell\|CaptionGesture"` | 37/37 |
| `dotnet test tests/Weavie.Linux.Tests` | 45/45 |
| GTK probes under Xvfb + openbox (`temp/review/probe/size.c`, `max.c`) | default size stays 800x600 across relaunch, while maximized, and while hidden |
| Bar measured in the built page, five chromes, 400px to 1280px | see R1, R9, R13, R21, R22 |
| CI on #1258 | CodeQL, platforms and web checks pass; .NET tests, the three native builds and e2e still running at the time of writing |

Fixes confirmed:

- **R1** holds. At 720px the Linux, Windows and macOS bars keep their height and every window button is
  visible; the browser page still folds its bar away. A Windows window under 760px now keeps its bar too.
- **R2** holds. `CaptureWindowState` saves GTK's default size, which `ApplyGeometry` always sets first.
  The old fallbacks for a maximized or hidden window are not needed: the default size is unchanged in both.
- **R3** holds for the three values GNOME offers.
- **R4** holds. One `CaptionGesture` in Core serves both hosts; eight Core tests cover it. The macOS use
  compiles here and runs only on CI.
- **R5** holds. No suppressions remain; one capture listener per page replaces the per-bar handlers.
- **R6** holds. No sleeps remain in the desktop spec; the drag steps until the window moves.
- **R7** holds. One copy each of the chrome rows, shell config, button names, report server, built-app
  guard and window-feature lookup.
- **R8** holds. The design matches the code on each listed point.
- **R9** holds. Left-side buttons at 1280px: the search box is centred and 514px wide. See R21 for the
  cost.
- **R11, R12, R14, R16, R18** hold as described in the fix notes.
- **R13** holds. A press on the logo now lands on `.tb-icon`.
- **R17** holds by construction: the build number is out of the drag region again, as before PR #1253.
  Not run on Windows.

New findings from the fixes:

### R21 — the search box shrinks to nothing with left-side buttons

`src/web/src/chrome/titlebar.css:9`, `src/web/src/chrome/TitleBar.tsx:28`

Both side columns now take the wider side's width, so the search box gets only what is left. With menus
and three left-side buttons the wider side is 375px, and the box is centred but:

| Window | Search box |
| --- | --- |
| 1280px | 514px |
| 1000px | 234px |
| 800px | 34px, with a 0px text field |

Below 760px the compact layout takes over and the box opens as an overlay. Layouts with the buttons on
the right are barely affected: the Windows row's wider side is 231px, which leaves 322px at 800px.

### R22 — the right end of the bar leaves a narrow window

`src/web/src/chrome/titlebar.css:9`

The grid cannot be narrower than twice the wider side. In a window narrower than that the right column
starts past its room and its content runs off the window. Before the fix the limit was the two sides
added together. Measured at the widths R1 made reachable:

- Two buttons left, one right (wider side 327px, limit 654px): at 500px the maximize button sits at
  608–654px, outside the window.
- Three buttons left (limit 750px): at 720px the build number is cut off; at 683px it is gone.
- Buttons on the right with menus (limit 462px): fine at 720px, clipped below 462px.

In the compact layout the search box is not in the bar at all, so the equal columns centre nothing there.


## Second fix stage

- **R21** fixed. The omnibar is centred only while that leaves it `--omnibar-min-width` (320px); below that it
  keeps 320px and slides off the wider side, then narrows between the sides. With three left-side buttons it
  is 320px at 1000px and 800px (was 234px and 34px).
- **R22** fixed. The bar needs at most both sides together, as before #1258, so every window button and the
  build number stay inside the window at 500px and 720px for every layout.
- Test: `the <layout> bar fits every window width` sweeps 500–1280px for the linux, linuxLeft, windows and
  mac rows. On #1258's layout it fails for both Linux rows (buttons outside the window; omnibar 234px and
  130px); with the fix all pass, and R9's centring at 1280px still holds.

## Second recheck

Fix commit `de794595` (PR #1258): `titlebar.css`, `TitleBar.tsx`, the browser spec and the design. Design and
scope are unchanged. Nothing was fixed in this stage.

| Check | Result |
| --- | --- |
| `npx vite build` on `de794595` | succeeds |
| `playwright test e2e/titlebar.spec.ts e2e/middle-click-autoscroll.spec.ts e2e/bridge.spec.ts --project chromium` | 45/45 |
| `biome check` on the three changed files | clean |
| Bar measured in the built page: windows, linux, linuxLeft, mac, browser at 14 widths from 380px to 1600px | see below |
| CI on `de794595` | platforms and web checks pass; .NET tests, the three native builds and e2e still running at the time of writing |

- **R21** holds. With three left-side buttons the search box is 514px at 1280px, 334px at 1100px, and 304px
  from 1000px down to 761px. It is never narrower than 304px above the compact breakpoint in any layout.
- **R22** holds. Every window button and the build number are inside the window at every measured width
  down to 380px, in every layout.
- **R9** still holds at the widths it was raised for: centred at 1280px in every layout. The fix for R21
  trades centring for width in narrower windows, as the design now says: the box sits right of centre
  below about 1070px with three left-side buttons (35px at 1000px, 135px at 800px), below about 975px with
  two, and only between 761px and 782px with the buttons on the right.
- **R1** still holds: the bar keeps its height and buttons in the compact layout in every desktop row.
- Below the two sides' combined width (about 415px with menus) the sides overlap by the shortfall; at 380px
  the right side covers 32–38px of the last menu. That limit predates both fixes.

### R23 — the width sweep restates the compact breakpoint

`src/web/e2e/titlebar.spec.ts:212`

`if (width > 760)` copies the breakpoint in `src/web/src/mobile/useCompactMode.ts:3`. If the breakpoint
moves, the search-box assertion is skipped or misapplied without a failure. The page's own `.app.compact`
class answers the same question.

