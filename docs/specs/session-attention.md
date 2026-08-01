# Session attention

**Status:** implemented for sounds, title badge, and browser OS notifications.

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
payload: {label, kind}
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

    S->>B: attention.raised {label, kind}
    B->>C: exact address
    C->>C: apply preferences and focus policy
    C->>P: sound; badge/OS notification when away
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

## OS notifications

Browser-hosted Weavie uses the Web Notification API. Permission is requested only from an explicit action in
a persistent toast, never automatically on load. Denial degrades to sound and title badge.

Notification identity is `(backendId, slot)`, so repeat events for one session coalesce. Clicking:

1. focuses the browser window;
2. dispatches `weavie.session.focus` with that host and slot;
3. selects the matching live `ClientSession`.

Native WebView shells currently surface one capability toast instead of silently pretending to create an OS
notification. Native notification adapters remain separate platform work; attention routing and sound do
not depend on them.

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
- clicking resolves the exact backend and session;
- removed/old session incarnations cannot generate client presentation;
- disabled per-kind and global settings are respected.

See [session-message-bus.md](session-message-bus.md) and
[remote-sessions.md](remote-sessions.md).
