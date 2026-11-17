# Unified title bar — brief

## Goal

On macOS and Linux the workspace window has one bar: Weavie's own, holding the search box and the window
controls. The OS title bar above it is gone, as it already is on Windows.

## What the user sees

| | Left | Centre | Right |
| --- | --- | --- | --- |
| Windows (unchanged) | logo, menus | search | build number, minimize / maximize / close |
| Linux | logo, menus | search | build number |
| macOS | native traffic lights | search | build number |

- **Linux window buttons** are drawn by Weavie and follow the desktop's button-layout setting: which side,
  which order, and which buttons exist. A desktop that configures none gets none. Close does what the OS
  close button does today: it quits. File ▸ Close Window still returns to the welcome screen.
- **macOS window buttons** are the real traffic lights, floated over the bar and centred in its height. The
  menus stay in the system menu bar. In fullscreen the traffic lights hide and the bar takes their space back.
- **A press on empty bar space** does what the same press does on a native title bar. Dragging moves the
  window, with snapping and dragging out of maximized owned by the OS. Double-click, and on Linux middle- and
  right-click, follow the system's title-bar settings (by default: maximize, and the window menu).
- The window keeps its shadow, rounded corners, edge resizing and tiling on both systems.
- **Linux welcome screen** has a slim bar of its own: draggable, with the window buttons and no search.
- **Browser client** no longer shows minimize / maximize / close buttons, which do nothing there today.

## Decisions

- One bar component serves every host. The search box and the menus are already single shared components
  and stay that way; the second, native-frame bar is deleted.
- The host describes its chrome and the page renders it: whether menus show, who draws the window buttons
  (the page, the OS, or nobody) and in what layout, and the space native buttons occupy. This replaces the
  platform name the page branches on today. The description is sent at boot and again when it changes.
- The page reports a press on empty bar space; the host decides what it means. The page never names
  "move" or "maximize", so no system policy is copied into Weavie.
- Linux buttons are drawn by Weavie, not by GTK. GTK's buttons come only in light and dark and would clash
  with a themed bar. macOS keeps the system's because they look the same in every app and carry the
  green-button tiling menu.
- The build number appears in the bar on macOS and Linux, as on Windows.
- The macOS window title is no longer visible in the window. It still names the window in Mission Control
  and the Window menu; the search box already shows the workspace name.
- No setting restores the OS title bar. It would keep the two-strip layout alive and tested on both systems.
  A desktop where the merged bar misbehaves gets a fix, not a switch.

## Edge cases

- Linux button layout changes while the app runs: the bar follows it.
- Linux maximized, tiled or fullscreen: no edge resize handles and no rounded corners, as the desktop dictates.
- An unfocused window dims the bar, as on Windows, and can be dragged by the bar without a first click.
- The menus, search box, build number and window buttons never start a window drag.
- A press report that reaches the host after the button is released does nothing.
- macOS traffic lights keep their place across resize, fullscreen entry and exit, and focus changes.

## Out of scope

- Windows behaviour.
- The welcome windows on macOS and Windows, which keep the OS title bar.
- Mobile, and the browser client beyond dropping its dead window buttons.
- Drawing custom window buttons on macOS.

## Approach

- **Windows drags through the web engine**, which lets a page mark a region as the window caption. The macOS
  and Linux web views have no such feature. There the page sends one "bar pressed" message, and the host
  pairs it with the native press it saw and hands the gesture to the OS. This is the one new message.
- **Linux** uses client-side decorations: the window declares that the app draws its own title bar. It is the
  standard on Wayland and X11 and every GTK app relies on it. Linux has one window that serves both the
  welcome screen and the workspace, and a visible window cannot reliably change who draws its title bar, so
  both get Weavie's. The host asks GTK for the button layout and the title-bar click settings; the desktop
  publishes them to GTK, so the host never identifies the desktop.
- **macOS** extends the web content under a transparent, untitled title bar and keeps the system's own
  buttons, so hover, the tiling menu and fullscreen need no code of ours. The host measures the space the
  buttons take and reports it; the page hardcodes no AppKit geometry.
- **Minimize, maximize and close** on Linux reuse the messages the Windows bar already sends. macOS sends
  none, and neither system uses the Windows edge-resize handles.
- Host-facing additions go in the shared host core.

## Proving it

Automated browser tests cannot drag a real window or see native buttons, so:

- The press handling and the traffic-light placement are built first, and the old strip is deleted only
  after that build has been tried by hand on macOS (windowed and fullscreen) and on a Linux desktop.
- The macOS CI job already launches a real app window. It gains a test that checks the traffic lights sit
  inside the bar and attempts a synthetic drag, and it uploads a screenshot of the bar. Whether a
  synthetic drag moves the window on a CI runner is unproven; the hand check stands either way.
- The bar's layouts are covered in the mock-host browser tests by injecting each chrome description.
