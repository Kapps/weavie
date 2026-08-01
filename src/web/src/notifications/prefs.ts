// The notifications.* settings, host-owned: injected before navigation and published by the local host's
// settings feature on change. Presentation happens locally, so remote hosts cannot replace this source.

import { hostInjected, type NotificationPrefs, registerHostFeature } from "../bridge";

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

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host
    .feature("settings")
    .on<NotificationPrefs>("notification-prefs", (prefs) => {
      current = prefs;
    });
});
