// The notifications.* settings, host-owned: injected as window.__WEAVIE_NOTIFICATIONS__ before navigation
// and re-pushed as { type: "notification-prefs" } on change — a local-machine push (like clipboard/
// window-state), so the page-serving backend is the one prefs source. See docs/specs/session-attention.md.

import { hostInjected, type NotificationPrefs, onHostMessage } from "../bridge";

export type { NotificationPrefs };

declare global {
  interface Window {
    /** Resolved notification prefs injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_NOTIFICATIONS__?: NotificationPrefs;
  }
}

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults: everything on, volume 70, the bundled pack.
const DEFAULT_PREFS: NotificationPrefs = {
  sounds: true,
  os: true,
  volume: 70,
  soundPack: "weavie",
  gates: { turnComplete: true, needsInput: true, failed: true },
};

let current: NotificationPrefs = hostInjected(
  "__WEAVIE_NOTIFICATIONS__",
  window.__WEAVIE_NOTIFICATIONS__,
  DEFAULT_PREFS,
);

/** The notification prefs to apply right now — read at each attention event, so changes apply live. */
export function notificationPrefs(): NotificationPrefs {
  return current;
}

onHostMessage((message) => {
  if (message.type === "notification-prefs") {
    const { type: _type, ...prefs } = message;
    current = prefs;
  }
});
