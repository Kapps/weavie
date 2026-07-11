// The notifications.* settings, host-owned: injected as window.__WEAVIE_NOTIFICATIONS__ before navigation
// and re-pushed as { type: "notification-prefs" } on change. Honored only from the page-serving (local)
// backend, so one prefs source governs presentation — binding a remote backend must not flip the client's
// sound/notification behavior. See docs/specs/session-attention.md.

import {
  hostInjected,
  LOCAL_BACKEND_ID,
  type NotificationPrefs,
  onSessionMessage,
} from "../bridge";

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
  onTurnComplete: true,
  onNeedsInput: true,
  onFailed: true,
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

onSessionMessage((message, backendId) => {
  if (message.type === "notification-prefs" && backendId === LOCAL_BACKEND_ID) {
    const { type: _type, ...prefs } = message;
    current = prefs;
  }
});
