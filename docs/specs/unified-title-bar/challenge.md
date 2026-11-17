# Unified title bar — challenge

First visit: there are no earlier objections to reconcile. Claims about GTK 4, AppKit or WebKit behaviour
that the repository cannot prove are marked **unverified** and need a spike before the design is fixed.

## 1. The "one new mechanism" gives the gesture to the wrong owner

The brief has the bar publish "start a window move" on mouse-down and reuses `maximize-toggle` for
double-click. Three problems, all in the same place.

- **The OS calls need the native press, which the web cannot send.** The Linux window menu
  (`gdk_toplevel_show_window_menu`) takes the `GdkEvent`; the AppKit drag takes the mouse-down `NSEvent`
  (unverified). So each host must record native pointer presses anyway. The brief's mechanism is really
  "a message plus an unstated native recorder".
- **Nothing ties the message to a press that is still down.** The only precedent,
  `src/web/src/chrome/ResizeFrame.tsx:26-35` → `src/Weavie.Win/Hosting/CustomChrome.cs:205-229`, sends on
  mouse-down with no liveness check and leans on Win32's modal loop. A move request that lands after the
  button is up may leave the window following the cursor on X11 or AppKit (unverified). Starting the move
  on the first press may also hide the release and the second click from the page, which breaks the
  double-click the brief asks for (unverified).
- **Double-click is an OS policy, not "maximize".** `IShellWindow.ToggleMaximize`
  (`src/Weavie.Core/Shell/IShellWindow.cs:12`) cannot say "minimize" or "do nothing". The brief follows
  the system setting on macOS and hardcodes maximize on Linux, where GTK has its own titlebar
  double-click, middle-click and right-click settings (unverified). That is a hardcoded copy of an
  authoritative source, in the same brief that insists on following the desktop's button layout.

Windows shows the working shape: the page only *declares* caption space
(`src/web/src/chrome/titlebar.css:16-17`) and the native side owns drag, double-click and the system menu
(`src/Weavie.Win/WorkspaceWindow.WebView.cs:68-70`).

| Alternative | Gains | Costs |
| --- | --- | --- |
| **A. Brief:** web names each action (`move`, `maximize-toggle`, window menu) | Smallest web change | Press/release race; OS policy copied into the web; still needs a native recorder for the menu |
| **B. Host hit-tests:** web publishes draggable rectangles, a native controller acts on the real event | No race, no round trip | A second copy of DOM hit-testing; every scrim, menu and popover over the bar must retract it, and a stale copy steals a press meant for a dialog |
| **C. Caption press (recommended):** web sends one `window.captionPress`; the host pairs it with the live native press it recorded and decides move, double-click action or window menu from that event | DOM stays the hit-test authority; the web never names "maximize"; a late message finds no live press and does nothing | A pointer recorder per host (Linux has the pattern at `src/Weavie.Linux/WorkspaceHost.Keyboard.cs:20-27`); press identity is still by liveness, so a late message can meet the *next* press |

The architect, consulted once on this decision, also picked C and flagged the same open points: press
identity, whether AppKit accepts a recorded mouse-down after drag events, and whether the GTK titlebar
gesture works outside GNOME. The brief should adopt C or say why the race and the policy copy are
acceptable, and name the spike that settles the unverified items.

## 2. Linux has no welcome window to exempt

The brief keeps the OS title bar on "the welcome window". On Linux there is one `GtkWindow`, created once
(`src/Weavie.Linux/WorkspaceHost.cs:95-108`), that turns into the workspace
(`src/Weavie.Linux/WorkspaceHost.Welcome.cs:45-50`) and back again
(`src/Weavie.Linux/WorkspaceHost.Shell.cs:25-29`). The brief therefore requires switching one presented
window between OS decorations and client-side decorations in both directions, which GTK documents as
unreliable once a window is visible (unverified). The alternatives:

- **Welcome gets the bar on Linux.** No live switch; contradicts the brief's scope and needs a welcome
  bar with window buttons and drag.
- **Switch decorations live.** Matches the brief's wording; rests on behaviour GTK warns about and must be
  proven on Wayland and X11.
- **A separate welcome window**, as macOS (`src/Weavie.Mac/WelcomeWindow.cs:15`) and Windows already
  have. Removes the question by construction; the largest change.

The same single-window design leaves "close" undefined. The bar's ✕ maps to `IShellWindow.Close`, but
Linux's File ▸ Close Window returns to welcome (`WorkspaceHost.Shell.cs:25-29`) while the OS close button
quits (`src/Weavie.Linux/WorkspaceHost.cs:163-167`). The brief must say which one the drawn ✕ does.

## 3. Drawing the Linux buttons re-implements what GTK already owns

For macOS the brief keeps the system buttons "so hover, the green-button tiling menu and fullscreen need
no code of ours". For Linux it does the opposite: read the layout, push it, re-push on change, draw the
buttons, and answer minimize / maximize / close. The host is already GTK 4
(`src/Weavie.Linux/Native/Gtk.cs:10`), which ships a window-controls widget that follows the layout
setting, its live changes and the theme by itself (unverified that it composes over the web view).

- **Brief (web-drawn):** Weavie's look on every desktop; costs a layout parser, a live push, three control
  handlers, and an unanswered design question — the brief's table has no Linux column for buttons, and a
  left-side layout collides with the logo and menus.
- **Native controls floated over the bar,** mirroring the macOS choice: layout, order, presence, theme and
  live changes cost nothing, and Linux needs no control messages at all. Costs a native overlay above the
  web view and a measured inset pushed to the page; the buttons look like the desktop, not like Weavie.

The brief should justify the asymmetry or take the second option.

## 4. "What differs is data" — but the data model is missing

The decision is right; the brief stops before defining the data.

- **The mode is a platform name.** `titleBar` is `"custom" | "mac" | "linux"`
  (`src/Weavie.Hosting/IHostPlatform.cs:29`), branched in `src/web/src/App.tsx:237-242`. One component fed
  a platform name is still three bars.
- **There are four consumers, not three.** Headless reports `"custom"`
  (`src/Weavie.Headless/HeadlessPlatform.cs:32`) with no window (`:41`), so the browser client renders
  minimize / maximize / close (`src/web/src/chrome/TitleBar.tsx:57-82`) whose messages are dropped by
  `_shell?.` (`src/Weavie.Hosting/HostCore.Messages.cs:193`). The brief puts the browser out of scope, yet
  its single component must still describe "no window buttons".
- **Live state is two booleans.** `window.state` carries `maximized` and `focused`
  (`src/Weavie.Hosting/HostCore.cs:398-399`, `src/web/src/chrome/window-state.ts:25`). The brief needs
  fullscreen (both systems), tiled (Linux), the button layout (Linux) and the traffic-light inset (macOS).
  Neither native host observes fullscreen today.
- **The traffic-light inset has no owner.** If the page hardcodes it, that is a copy of AppKit's geometry;
  it is also on the right in right-to-left system languages (unverified), which the brief's "Left" column
  rules out. The host should measure and push it.

Missing abstraction: one host-owned chrome description — who draws the buttons (page, OS, nobody), their
side and order, the reserved insets, whether menus show, whether the caption is declared or pressed —
sent at boot and re-sent on change through `HostCore`, replacing both `titleBar` and the two-boolean push.
The four hosts then differ only in the record they fill, and the browser's dead buttons go away.

## 5. "Reuse the window-control messages" over-claims

- macOS never sends minimize or close: the traffic lights are native. Its double-click is an OS action,
  not `maximize-toggle` (objection 1).
- Neither system needs `window.resize`: the brief keeps native edge resizing on both. `ResizeFrame` is
  mounted for the Windows frameless window only (`src/web/src/App.tsx:2042-2044`). Giving macOS and Linux
  an `IShellWindow` means a `StartResize` (`src/Weavie.Core/Shell/IShellWindow.cs:18`) that does nothing.
- `IHostPlatform.Window` is `IShellWindow?` (`src/Weavie.Hosting/IHostPlatform.cs:38`), against the
  no-nullable-dependencies rule. After this change only Headless is null, so the brief should require the
  dependency and split it: buttons (minimize / maximize / close), frameless resize (Windows), caption
  press (macOS, Linux). Each host then implements only what it does.

## 6. No way to prove it is stated

- No end-to-end spec selects either bar; two specs only set the mode as fixture data
  (`src/web/e2e/bridge.spec.ts:72`, `src/web/e2e/middle-click-autoscroll.spec.ts:28`).
- The headless host can only render the Windows variant (`HeadlessPlatform.cs:32`), so the Linux and macOS
  layouts are reachable in tests only if the chrome description (objection 4) is injectable.
- Drag, snapping, the window menu and traffic-light placement cannot be driven by Playwright. The brief
  needs a named manual matrix — at least GNOME on Wayland, KDE, an X11 session, and macOS windowed and
  fullscreen — and should run it as a spike before `NativeTitleBar` is deleted.

## 7. "Does not depend on the desktop" is asserted, not shown

The brief treats client-side decorations as uniform and refuses a setting that restores the OS bar.
Refusing the setting is defensible: it would keep both layouts alive. But it removes the only recourse on
a desktop where the result is wrong — a compositor that prefers server-side decorations, a tiling window
manager, an X11 session with no compositor for shadows, or rounded corners that do not clip the web view
(all unverified). With no fallback allowed, the matrix in objection 6 is the gate, and the brief should
say what ships if one desktop fails it.

## Smaller points

- **macOS button placement.** Moving the traffic lights and re-applying on every relayout is the fragile
  part the brief already senses ("keep their place across resize, fullscreen entry and exit"). A compact
  unified toolbar lets AppKit centre them in a taller title bar (unverified); the bar's height then comes
  from the host rather than the `38px` in `src/web/src/chrome/titlebar.css:12`.
- **Inactive-window drag.** A native title bar drags without a first activating click; a web view may eat
  that click (unverified). The brief lists unfocused dimming but not this.
- **Linux geometry.** Saved bounds are trusted with no screen check (`src/Weavie.Linux/WorkspaceHost.cs:125-128`).
  Client-side decorations add a shadow margin; the brief should say whether saved sizes mean the window or
  its content.

## Is there a materially better approach?

Yes, in three places, without changing the goal: the host decides the caption gesture from a native press
(1C); the description of the chrome is one host-owned record instead of a platform name (4); and Linux
keeps the desktop's own buttons as macOS keeps its own (3). Together they shrink the web protocol to one
new message and one record, and delete the layout parser, the Linux control handlers and the nullable
window. The price is more native code per host and a Linux bar that looks like the desktop.
