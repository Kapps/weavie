# Session attention

**Status:** implemented for sounds, title badge, browser notifications, and native Windows, macOS, and
Linux notifications.

When an agent completes, needs input, or fails, its owning session raises an attention event. The web
presents that event on the user's machine even when the session is in the background or belongs to a remote
host.

## Classification

`AttentionRules` classifies transitions from each session's `SessionStatusMachine`:

| transition | kind |
| --- | --- |
| `Working → Idle` | `turnComplete` |
| any transition into `NeedsInput` | `needsInput` |
| any transition into `Error` | `failed` |

Startup, ordinary waiting, and return-to-work transitions do not raise attention.

## Ownership and delivery

`HostCore.WireAttention` subscribes to every loaded `HostSession`. On a classified transition it publishes:

```text
scope: session
feature: attention
name: raised
payload: {label, kind, body}
```

The exact session address is in the envelope. The host does not inspect focus, selection, active backend, or
view binding. This is essential: an unfocused/background completion is the primary use case.

`registerSessionFeature` installs the web intake on every `ClientSession`, including sessions added later and
sessions on remote hosts.

```mermaid
sequenceDiagram
    participant S as HostSession status
    participant B as session bus
    participant C as owning ClientSession
    participant P as local presenter
    participant O as local OS

    S->>B: attention.raised {label, kind, body}
    B->>C: exact address
    C->>C: apply preferences and focus policy
    C->>P: sound; badge/notification when away
    P->>O: browser API or local native host channel
```

## Presentation policy

Presentation is the only layer allowed to consult focus and selection:

| window focused | event owner selected | sound | OS notification |
| --- | --- | --- | --- |
| yes | yes | no | no |
| yes | no | yes | no |
| no | either | yes | yes |

The event has already reached and been processed by its owner before this policy runs. Suppression prevents
redundant presentation; it never drops domain state.

`windowFocused()` combines document focus, visibility, and native window focus where available. An
unfocused event also sets a title badge, cleared when the window regains focus.

## Sound

The client owns audio, so remote worker events are audible locally. The bundled `weavie` pack uses the CESP
manifest format:

- `task.complete`;
- `input.required`;
- `task.error`.

The player reads current preferences for every event, picks an entry in the mapped category, and applies the
configured volume. A failed pack load or browser autoplay rejection is surfaced as a keyed toast.

OS notifications are silent; the pack player is the one audio source.

## Notifications

Browser-hosted Weavie uses the Web Notification API. Permission is requested only from an explicit action in
a persistent toast, never automatically on load. Denial degrades to sound and title badge.

Native shells send presentation requests to the local `HostCore`; remote workers never present on their own
desktop. The local core owns replacement and activation identities, and each platform channel uses its native
surface:

| shell | native surface |
| --- | --- |
| Windows | silent, clickable `Shell_NotifyIcon` notification-area banners |
| macOS | `UNUserNotificationCenter` |
| Linux | `org.freedesktop.Notifications` desktop service |

macOS authorization is requested only from the persistent toast's **Enable** action. Windows and Linux report
the authorization/service state without manufacturing a prompt. An unavailable service or delivery failure is
shown as an error toast rather than silently dropping the event.

Windows remains an unpackaged, self-contained folder/ZIP. Its notification API is built into Windows and
requires no App SDK bundle, MSIX, Start-menu shortcut, registry setup, or installer.

Linux also remains a portable folder/archive. On launch, Weavie maintains its per-user desktop entry with an
absolute `Exec` path into the folder. The host calls the freedesktop notification service directly, passing
that desktop-entry identity and retaining the returned server id for replacement and click routing.

Replacement identity is stable for one `(page, backendId, slot)`, so repeat events coalesce without allowing
one page or remote backend to replace another's notification. Each delivery receives a fresh activation
identity tied to the exact `{backendId, slot, incarnation}`. Clicking:

1. activates the owning native workspace window (or browser window);
2. unicasts the exact activation route to the page that presented it;
3. dispatches `weavie.session.focus` with that backend, slot, and incarnation;
4. selects the session only if that exact incarnation is still live.

Disconnecting a page or disposing its host removes its native notifications and routes. All OS notifications
are silent; sound remains owned by the web client so local and remote events have the same audio behavior.

## Settings

All settings are live, user-scoped values:

| key | default |
| --- | --- |
| `notifications.sounds` | `true` |
| `notifications.os` | `true` |
| `notifications.volume` | `70` |
| `notifications.soundPack` | `weavie` |
| `notifications.onTurnComplete` | `true` |
| `notifications.onNeedsInput` | `true` |
| `notifications.onFailed` | `true` |

The local host is the preference source because presentation occurs on the local machine. Remote hosts'
notification settings are not allowed to replace it.

## Required coverage

- the transition classifier includes positive and negative cases;
- every loaded session publishes on its own bus;
- a background or remote completion reaches intake and plays sound;
- a focused selected session is presentation-suppressed;
- an unfocused event sets the badge and creates a browser notification when permitted;
- each native channel reports permission and presents the canonical title/body;
- native replacement identity remains stable while activation identity is fresh;
- clicking resolves the exact page, backend, session incarnation, and owning native window;
- disconnect and shutdown remove native notifications and activation routes;
- removed/old session incarnations cannot generate client presentation;
- disabled per-kind and global settings are respected.

See [session-message-bus.md](session-message-bus.md) and
[remote-sessions.md](remote-sessions.md).
