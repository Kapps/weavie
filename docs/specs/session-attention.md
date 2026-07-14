# Session attention: sounds + OS notifications

When a session needs the user — a turn finished, a permission prompt is waiting, claude crashed —
Weavie escalates past the rail chip: a short sound, and (when the window isn't focused) an OS
notification that **names the session and takes you to it when clicked**. That click-to-focus routing
is the differentiator over a generic Claude Code sound plugin; the multi-session spec already names
this as "the real value" (see [multi-session-and-worktrees.md](multi-session-and-worktrees.md)).

## Why

Weavie's thesis is running several sessions and *not* babysitting them. Today the only "your session
wants you" signal is a rail chip changing color — invisible when the window is behind your browser,
another app, or on another monitor. Existing tools (peon-ping and its OpenPeon/CESP pack ecosystem)
prove the demand for audible completion cues, but they play audio via hooks **on the machine running
`claude`** — which in Weavie's remote mode is a headless worker. Presentation must live in the web
layer, the only process next to the user (see
[architecture-overview](../concepts/architecture-overview.md): rendering always happens in the web
layer).

## Goals / non-goals

- **Goal**: sound + OS notification on session attention events, presented on the **client**, for
  every loaded session on every connected backend — background and remote sessions included.
- **Goal**: clicking a notification activates the window and focuses that specific session.
- **Goal**: the built-in sounds are a CESP pack (the OpenPeon format) so user-installable packs and
  per-session packs slot in later without a format migration.
- **Non-goal (v1)**: user-installed pack discovery, per-session pack identity, a pack picker UI —
  deferred, but the seams for them are named below.
- **Non-goal**: using the webview Notification API inside the desktop shells (permission grants,
  WKWebView polyfills). Shell-served notifications route through the local host — the `open-url`
  pattern — with the per-OS implementations landing as phase 2.
- **Non-goal**: an attention *log*. Attention is transient; the rail chip remains the persistent state.

## Event model

Attention events are classified from `SessionStatusMachine` transitions
(`src/Weavie.Core/Sessions/SessionStatusMachine.cs`) — the machine already derives per-session status
from the hook stream and supervisor; nothing new observes claude.

```csharp
public enum AttentionKind { TurnComplete, NeedsInput, Failed }

// Pure, unit-testable: null = no attention.
public static AttentionKind? Classify(SessionStatus previous, SessionStatus next);
```

| Transition | Kind | CESP category |
|---|---|---|
| `Working → Idle` (Stop hook, no resume) | `TurnComplete` | `task.complete` |
| `* → NeedsInput` (permission prompt / idle notice) | `NeedsInput` | `input.required` |
| `* → Error` (supervisor crash) | `Failed` | `task.error` |
| `Starting → Idle`, `Working → Waiting`, `NeedsInput → Working`, all others | — (no event) | |

## Pipeline

```mermaid
sequenceDiagram
    participant SM as SessionStatusMachine (Core)
    participant HC as HostCore.Attention (Hosting)
    participant AT as attention.ts (web intake)
    participant P as sounds.ts / presenter.ts

    SM->>HC: Status.Changed (every loaded session)
    HC->>HC: AttentionRules.Classify(prev, next)
    HC->>AT: push session-attention {slot, label, kind}
    AT->>AT: settings gate + focus suppression
    AT->>P: play(pack, kind); notify(title, tag) if window unfocused
    P-->>AT: notification click
    AT->>HC: weavie.session.focus {id, backendId} → switch-session
```

- **Core** (`Sessions/AttentionRules.cs`): the classifier above. No I/O, no state beyond the enum.
- **Hosting** (`HostCore.Attention.cs`): wired from `WireSession`
  (`src/Weavie.Hosting/HostCore.Sessions.cs:98`), which already observes `Status.Changed` for every
  loaded session, never active-gated. The previous status is a closure variable in the wiring; the
  machine's event signature doesn't change. On a hit, resolve the slot id and
  `PostToWeb({type:"session-attention", slot, label, kind})`.
- **Cross-backend**: `session-attention` joins `isSessionMessage()` (`src/web/src/bridge.ts:686`), so
  the push arrives from *every* connected backend tagged with `backendId` — a remote worker's ping
  reaches the local client exactly like its rail chips do. This is what makes remote sounds local.
- **Web intake** (`notifications/attention.ts`): the single entry point. Applies the per-event
  setting gates and the delivery matrix below, then fans out to `sounds.ts` and `presenter.ts`.

## Delivery matrix

Sound is the constant; the OS notification is the escalation for when you're away. Exactly one of
each, ever.

| Window focused? | Event session is the active chip? | Sound | OS notification |
|---|---|---|---|
| yes | yes | — | — |
| yes | no | ✔ | — |
| no | (any) | ✔ | ✔ |

Focus = `windowFocused()` (`chrome/window-state.ts`): `document.hasFocus()` ∧ page visible ∧ the
shell's last `window-state {focused}` push (true where no shell pushes one). Inside WebView2/WKWebView
`document.hasFocus()` keeps reporting true after the native window is minimized or deactivated, so
visibility and the host push corroborate it — any signal saying "away" wins. Active session = active
`backendId` + active rail chip from `session-store.ts`.

## One owner of audio

OS notifications normally carry the platform's own alert sound, which would double up with the pack
sound and vary by OS. Rule: **Weavie's player owns all audio; every notification is raised
`silent: true`** (supported in all current browsers — Chrome 43+, Edge 17+, Firefox 132+,
Safari 16.6+), and the shell path's `IHostNotifications` implementations post their native
notifications sound-free for the same reason. This keeps the sound identical whether or not a
notification accompanies it, keeps it working in the sound-only row of the matrix, and keeps volume
under Weavie's control.

## Sounds

`notifications/sounds.ts` is a minimal CESP player: resolve category → pick a random entry from the
pack manifest → `new Audio(url)` at the configured volume. A rejected `play()` (browser autoplay
policy before first interaction) raises one keyed `notify()` toast — a blocked sound is surfaced,
never swallowed.

The built-in pack lives at `src/web/public/sounds/weavie/` as a **real CESP pack**
(`openpeon.json` manifest + `sounds/`), served same-origin by both the native virtual host and
Kestrel. v1 content: original synthesized tones — calm, relaxed, short, quiet:

- `task.complete` — soft two-note ascending chime, ~400 ms, gentle attack. The subtlest of the three.
- `input.required` — three-note rising motif, ~600 ms, slightly more present: this one asks you to act.
- `task.error` — low two-note descending, muted, ~500 ms. Distinct, not alarming.

**Licensing line**: the popular OpenPeon packs are game-ripped audio with unvetted self-declared
licenses. Weavie may *support the format* and later let users install packs themselves; it must never
bundle third-party pack audio. Built-in sounds are original.

**Deferred pack seams**: pack choice funnels through one `packFor(event)` resolution — per-session
packs (audible session identity) later replace its body. User-installed packs live on the host
(`~/.openpeon/packs/`) and need a same-origin asset endpoint to reach a remote client; that endpoint
is phase 3 with the picker UI.

## OS notifications

`notifications/presenter.ts` splits on `isBrowserHostedShell()` exactly like `openUrlExternal`
(`src/web/src/terminal/terminal-links.ts:41`) — the codebase's existing pattern for "present this
next to the user":

- **Browser-served** (remote mode, local Kestrel in a tab): the tab runs in the user's real browser,
  so the Web Notification API *is* the native path — notifications land in the OS notification
  center. The permission prompt is the browser's security gate, not ours to waive — handled below.
- **Shell-served**: post `show-notification {tag, title, body}` to the **local** host — like
  `open-url`, never to a remote backend. The host posts a real OS notification as Weavie (its own
  icon and name, no web-permission prompt; macOS's standard one-time app authorization is the only
  OS gate). On activation the host focuses the window natively and posts
  `notification-activated {tag}` back, routed into the same click-to-focus path. The webview
  Notification API is never used in shells — no permission grants, no WKWebView polyfill. Until the
  per-OS implementations land (phase 2), shell-served Weavie surfaces one keyed toast naming the gap
  — v1 ships no dead channel.

| Client | Path | When |
|---|---|---|
| Browser (remote mode, local Kestrel) | Web Notification API | v1 |
| Native shells — Win (toasts), Mac (`UNUserNotificationCenter`), Linux (`GNotification`) | `show-notification` → local host via `IHostNotifications` | phase 2, per-OS |

The host seam is `IHostNotifications? Notifications` on `IHostPlatform`
(`src/Weavie.Hosting/IHostPlatform.cs`, mirroring the nullable `Dialogs`/`Window` pattern):
`Show(tag, title, body)` + an `Activated` event. Whether the platform implements it is advertised in
the shell bootstrap; a shell without it + `notifications.os` on → one keyed toast naming the gap —
never send-and-silently-drop.

**Browser permission is never requested cold** (shell-served Weavie never shows a web-permission
prompt at all). On the first attention event with `notifications.os` enabled and browser permission
still `default`, the presenter shows one keyed action toast whose button click is the user gesture
that triggers `Notification.requestPermission()` (the full prompt, not Chrome's quieter fallback).
The toast is raised while the user is away, so it **persists** — action toasts are exempt from
auto-dismiss — until acted on or dismissed. A denial is remembered by the browser itself, never
re-asked; an explicit dismissal holds for the page's lifetime; sounds and the title badge work
either way. The real control surface stays `notifications.os`; the browser prompt is a one-time
unlock, not a setting.

**Title badge**: while any session wants attention and the window is unfocused, prefix
`document.title` with `●` (cleared on focus). Permission-free, and the degrade path when browser
notifications are denied.

Browser mechanics: `new Notification(label, {body, tag: `${backendId}:${slot}`, silent: true,
renotify: true})` — the tag coalesces repeat pings from the same session. Click → `window.focus()` +
dispatch `weavie.session.focus`. The shell path carries the same tag through `show-notification` /
`notification-activated` so both paths converge on one click handler.

## Click-to-focus

New command `weavie.session.focus` (`RunsIn = Web`, args `{id, backendId}`, `ShowInPalette = false` —
the palette path for humans stays `weavie.session.switch`'s omnibar; this one is programmatic, and
Claude gets it for free via `runCommand`). The web handler resolves the session and calls the existing
`switchToSession` (`src/web/src/App.tsx:403`), which already handles cross-backend binding →
`switch-session` → `SwitchToSlot`.

## Settings

All in `Configuration/NotificationSettings.cs` (mirrors `FontSettings`: `Keys`, `Register`,
`BuildJson`; bootstrap via `__WEAVIE_NOTIFICATIONS__`, Live re-push on change as `notification-prefs` —
a local-machine push, routed like clipboard/window-state, so the page-serving backend is the one prefs
source). The per-event gates ride the JSON keyed by wire kind name, so the web indexes them by an
event's kind directly.

| Key | Type | Default |
|---|---|---|
| `notifications.sounds` | Bool | `true` |
| `notifications.os` | Bool | `true` |
| `notifications.volume` | Int 0–100 | `70` |
| `notifications.soundPack` | String (AllowedValues = pack catalog) | `weavie` |
| `notifications.onTurnComplete` | Bool | `true` |
| `notifications.onNeedsInput` | Bool | `true` |
| `notifications.onFailed` | Bool | `true` |

All `ApplyMode.Live`, `SettingScope.User`. Everything-on defaults are deliberate: the delivery matrix
already mutes the session you're watching, and turn-complete notifications match peer tools (Codex).

## Tests

- **Core unit** (`AttentionRulesTests`): the full transition matrix, especially the non-events
  (`Starting → Idle`, `Working → Waiting`, `NeedsInput → Working`). `NotificationSettingsTests`
  mirrors `FontSettingsTests`.
- **Hosting seam** (`SessionAttentionTests` via `TestHost`/`FakeHostBridge`): drive the status machine
  (prompt-submitted → turn-stopped) and assert the exact `session-attention` JSON at `PostToWeb` —
  the deterministic "event reached the client" assertion, at the same seam the WSS rides.
- **e2e** (`src/web/e2e/functional/notifications.spec.ts`, `headless` project, fake claude): stub the
  sinks with `page.addInitScript` (record `Notification` constructions with permission pre-granted;
  record `HTMLMediaElement.play`) — assert decisions, never real audio/OS UI. Journeys: turn-complete
  ping with focus stubbed away; permission-blocked → `needs-input` ping; **two sessions, background
  session's Stop → notification names it → invoke its `onclick` → active chip switches**; suppression
  (focused + active → recorder stays empty, asserted after a later observable event, not a timeout).
- **Remote delta**: nothing transport-specific beyond what `session-list` already proves; the
  two-session journey may run once on `remote` as the cross-transport smoke.

## Build phases

1. **Core**: `AttentionRules` + `NotificationSettings` + registration; unit tests.
2. **Host push**: `HostCore.Attention.cs`, bootstrap injection, Live re-push; hosting seam test.
3. **Web sounds**: bridge message types, `prefs.ts`, `attention.ts`, `sounds.ts`, the built-in tone
   pack; suppression.
4. **Notifications + focus**: `presenter.ts`, `weavie.session.focus`; e2e journeys.
5. **Phase 2 (per-OS PRs)**: the `show-notification` / `notification-activated` channel + its
   presenter branch, and the `IHostNotifications` implementations — Linux (`GNotification`),
   Windows (toasts), Mac (`UNUserNotificationCenter`).
6. **Packs**: user pack discovery + asset endpoint, pack picker, per-session pack identity.

## Decisions log

- Turn-complete pings on by default with the *subtlest* sound (peer parity with Codex).
- Focused window + background session → sound only, no OS notification.
- Notifications always `silent: true`; the pack player is the only audio source.
- Presentation splits on `isBrowserHostedShell()` — the `openUrlExternal` pattern: browser-served →
  Web Notification API; shell-served → `show-notification` to the LOCAL host (never a remote
  backend), posted natively with Weavie's own attribution and no web-permission prompt. The webview
  Notification API is never used inside shells.
- The browser permission request is gesture-gated behind a one-time toast, never fired on load;
  denial degrades to sounds + title badge and is never re-asked.
- v1 audio is original synthesized tones; no third-party pack audio is ever bundled.

## Open questions

- Only the Windows shell pushes `window-state {focused}` today. On Mac/Linux, minimize is caught by
  page visibility, but an app switch that leaves the window visible still depends on the webview's
  `document.hasFocus()` — wiring those shells' focus pushes closes the remaining gap.
- "Long-running command finished" as a fourth event: no clean duration signal exists today; excluded
  rather than faked.
